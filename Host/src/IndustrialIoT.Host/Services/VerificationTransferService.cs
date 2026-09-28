namespace IndustrialIoT.Host.Services;

using System.Diagnostics;
using System.Security.Cryptography;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;

/// <summary>
/// One request, one captured configuration, one driver, one owned file. HTTP form buffering is
/// disk-backed above 64 KiB; hashing/upload/read-back use bounded streams, never a file-sized array.
/// This is intentionally separate from normal program transfer (no resume, execute, or overwrite).
/// </summary>
public sealed partial class VerificationTransferService(
    IProtocolDriverFactory factory, IDeviceRepository devices, ILogger logger)
{
    public async Task<VerificationTransferResult> ExecuteAsync(string deviceId, VerificationTransferRequest request,
        Stream source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Approval is mandatory even for direct/internal calls, before resolving or connecting a driver.
        if (!request.AllowWrites) return Empty("未许可文件写入；没有创建目录、文件或连接传输目标。");
        if (!VerificationTransferPolicy.IsDirectory(request.Directory) || !VerificationTransferPolicy.IsFileName(request.RemoteName)
            || request.ExpectedBytes is <= 0 or > VerificationTransferPolicy.MaximumFileBytes
            || !IsHash(request.ExpectedSha256) || !IsHash(request.SnapshotToken))
            return Empty("验证目录、文件名、大小、SHA256或配置快照无效；未访问设备。");
        if (!source.CanSeek || source.Position != 0 || source.Length != request.ExpectedBytes)
            return Empty("实际请求附件长度与声明不符；未访问设备。");

        // Validate the complete current request BEFORE any remote writes. Form file streams are
        // immutable for this request. Do not replace a missing attachment with a generated payload.
        using var original = new HashSink(request.ExpectedBytes);
        await source.CopyToAsync(original, 81920, ct);
        var sourceHash = original.CompleteHash();
        if (original.Bytes != request.ExpectedBytes || !HashEquals(sourceHash, request.ExpectedSha256))
            return Empty("本轮请求附件实际大小或内容SHA256不匹配；未访问设备。");
        source.Position = 0;

        var captured = await CaptureAsync(deviceId, ct);
        if (captured is null) return Empty("设备或关联传输设备不存在；未访问设备。");
        var snapshot = ToSnapshot(captured);
        if (!HashEquals(snapshot.Token, request.SnapshotToken))
            return Empty("设备传输配置在本轮快照后变化（含凭据/关联设备）；停止测试，不向新目标写入。", snapshot, configurationChanged: true);
        if (!SupportsSafeProtocol(captured.Protocol)) return Empty("实际协议尚无验证专用原子创建契约；FOCAS内部程序号不能用唯一文件名保护。", snapshot);

        IProtocolDriver? driver = null;
        IVerificationFileLease? lease = null;
        var attempted = false;
        long uploaded = 0;
        double uploadSeconds = 0;
        VerificationTransferResult result = Empty("没有取得文件传输证据。", snapshot);
        try
        {
            driver = factory.Create(captured.Protocol, captured.Brand, captured.Model);
            if (driver is not IVerificationFileTransfer safeTransfer)
                return Empty("实际驱动未实现验证专用原子创建契约。", snapshot);
            // Captured is a deep copy, including credentials/options. Never resolve the device again
            // between mkdir, upload, read-back, cleanup, or reconnect within this request.
            var connected = await driver.ConnectAsync(captured.Config, ct);
            ct.ThrowIfCancellationRequested();
            if (!connected.Success) return Empty("本轮传输连接失败；尚未上传，没有文件测量证据。", snapshot, supported: true);
            lease = await safeTransfer.CreateVerificationFileAsync(request.Directory, request.RemoteName, ct);
            ct.ThrowIfCancellationRequested();
            attempted = true;
            var watch = Stopwatch.StartNew();
            try { uploaded = await lease.UploadAsync(source, request.ExpectedBytes, ct); }
            finally { watch.Stop(); uploadSeconds = watch.Elapsed.TotalSeconds; }
            ct.ThrowIfCancellationRequested();
            using var readBack = new HashSink(request.ExpectedBytes);
            await lease.DownloadAsync(readBack, ct);
            var readHash = readBack.CompleteHash();
            var matches = uploaded == request.ExpectedBytes && readBack.Bytes == request.ExpectedBytes
                && HashEquals(sourceHash, readHash);
            result = new()
            {
                Supported = true, HasEvidence = true, Success = matches, IntegrityVerified = matches,
                DeviceId = deviceId, SnapshotToken = snapshot.Token, Protocol = snapshot.Protocol,
                RemoteName = request.RemoteName, RemotePath = lease.RemotePath,
                UploadedBytes = uploaded, DownloadedBytes = readBack.Bytes, UploadSeconds = uploadSeconds,
                SourceSha256 = sourceHash, ReadBackSha256 = readHash,
                Detail = matches ? "本轮设备上传并回读完成，实际大小及SHA256一致。" : "本轮上传/回读大小或SHA256不一致。",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failed mkdir/connection is not an observed file result. An attempted upload/read
            // failure is evidence of this run, not an Unsupported success or an invented checksum.
            result = new()
            {
                Supported = true, HasEvidence = attempted, Success = false, IntegrityVerified = false,
                DeviceId = deviceId, SnapshotToken = snapshot.Token, Protocol = snapshot.Protocol,
                RemoteName = request.RemoteName, RemotePath = lease?.RemotePath ?? "",
                UploadedBytes = uploaded, UploadSeconds = uploadSeconds, SourceSha256 = sourceHash,
                Detail = attempted ? $"本轮文件上传/回读失败（{ex.GetType().Name}）。" : $"未取得原子独占测试路径（{ex.GetType().Name}）；未上传、不复用既有路径。",
            };
            logger.LogWarning("Verification transfer {Stage} failed for device {DeviceId}: {ErrorType}",
                attempted ? "round-trip" : "acquisition", deviceId, ex.GetType().Name);
        }
        finally
        {
            if (lease is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await lease.CleanupAsync(cleanup.Token).WaitAsync(cleanup.Token);
                    result.CleanupSucceeded = true;
                    result.Detail += " 本轮独占对象已清理。";
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    result.CleanupSucceeded = false;
                    result.Detail += $" 本轮独占对象清理未确认（{ex.GetType().Name}），需人工核对：{lease.RemotePath}。";
                    // Request cancellation prevents returning JSON; retain cleanup evidence in Host logs.
                    logger.LogWarning("Verification cleanup unconfirmed for {DeviceId}, owned path {Path}: {ErrorType}",
                        deviceId, lease.RemotePath, ex.GetType().Name);
                }
            }
            if (driver is not null)
            {
                try { await driver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { logger.LogWarning("Verification driver disposal failed for {DeviceId}: {ErrorType}", deviceId, ex.GetType().Name); }
            }
        }
        return result;
    }

    private static bool IsHash(string text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);
    private static bool HashEquals(string a, string b) => IsHash(a) && IsHash(b)
        && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a), Convert.FromHexString(b));

    private static VerificationTransferResult Empty(string detail, VerificationTransferSnapshot? snapshot = null,
        bool supported = false, bool configurationChanged = false) => new()
    {
        DeviceId = snapshot?.DeviceId ?? "", SnapshotToken = snapshot?.Token ?? "", Protocol = snapshot?.Protocol ?? "",
        Supported = supported, Detail = detail, ConfigurationChanged = configurationChanged,
    };

    // Writable, non-seekable sink understood by the FTP streaming API. No downloaded file is kept.
    private sealed class HashSink(long expectedBytes) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long Bytes { get; private set; }
        public string CompleteHash() => Convert.ToHexString(_hash.GetHashAndReset());
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > expectedBytes - Bytes) throw new IOException("Actual file is longer than the declared verification length.");
            _hash.AppendData(buffer);
            Bytes += buffer.Length;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    }
}

public sealed class VerificationTransferRequest
{
    public bool AllowWrites { get; init; }
    public string SnapshotToken { get; init; } = "";
    public string Directory { get; init; } = "";
    public string RemoteName { get; init; } = "";
    public long ExpectedBytes { get; init; }
    public string ExpectedSha256 { get; init; } = "";
}

public sealed class VerificationTransferResult
{
    public bool Supported { get; init; }
    public bool HasEvidence { get; init; }
    public bool Success { get; init; }
    public bool IntegrityVerified { get; init; }
    public bool ConfigurationChanged { get; init; }
    public string DeviceId { get; init; } = "";
    public string SnapshotToken { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string RemoteName { get; init; } = "";
    public string RemotePath { get; init; } = "";
    public long UploadedBytes { get; init; }
    public long DownloadedBytes { get; init; }
    public double UploadSeconds { get; init; }
    public string SourceSha256 { get; init; } = "";
    public string ReadBackSha256 { get; init; } = "";
    public bool? CleanupSucceeded { get; set; }
    public string Detail { get; set; } = "";
}
