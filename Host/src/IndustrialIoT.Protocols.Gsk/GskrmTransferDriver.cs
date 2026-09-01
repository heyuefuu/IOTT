namespace IndustrialIoT.Protocols.Gsk;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

/// <summary>
/// 广数 GSK RM 程序文件传输驱动 — 基于 GSKRM_SendCNCFile / GSKRM_ReceiveCNCFile。
///
/// 与 <see cref="GskrmDriver"/> 共用同一个 <c>gskrm.dll</c>，但各自持有独立句柄，
/// 以避免数据采集循环与文件长传输相互阻塞。Upload/Download 都是 SDK 内部整文件
/// 同步调用 — 不支持断点续传（<see cref="SupportsResume"/>=false），仅在传输开始
/// / 结束处触发一次 <see cref="IProgress{T}"/> 回调。
///
/// 底层 DLL 为 x86 时：见 <c>native/README.md</c>。
/// </summary>
[ProtocolDriver(ProtocolType.GskrmFileTransfer, "广数", "广州数控", "GSK", "MICRO-T400")]
public sealed class GskrmTransferDriver : IProtocolDriver, IAddressSpaceBrowser, IProgramFileBrowser, INCProgramTransfer
{
    private const int DefaultPort = 6000;   // [unverified]
    private const int DefaultTimeoutMs = 10_000;
    private const string TempTransferPrefix = "gskrm-xfer-";

    private readonly ILogger<GskrmTransferDriver> _logger;
    private readonly IGskrmApi _api;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private int _handle;
    private ConnectionState _state = ConnectionState.Disconnected;
    private DeviceConnectionConfig? _config;

    public ProtocolType Protocol => ProtocolType.GskrmFileTransfer;
    public ConnectionState State => _state;
    public bool SupportsResume => false;
    public DriverCapabilities Capabilities => DriverCapabilities.FileTransfer | DriverCapabilities.Browse;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public GskrmTransferDriver(ILogger<GskrmTransferDriver> logger, IGskrmApi? api = null)
    {
        _logger = logger;
        _api = api ?? new NativeGskrmApi();
    }

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_state == ConnectionState.Connected) return new() { Success = true };
            SetState(ConnectionState.Connecting);
            _config = config;

            var port = config.Port > 0 ? config.Port : DefaultPort;
            var timeout = config.ConnectTimeout.TotalMilliseconds > 0
                ? (int)config.ConnectTimeout.TotalMilliseconds : DefaultTimeoutMs;

            int rc = await Task.Run(() => _api.CreateInstance(config.Host, port, timeout, out _handle), ct);
            if (rc != GskrmErrorCodes.Ok || _handle <= 0)
            {
                var msg = $"GSKRM_CreateInstance failed: {GskrmErrorCodes.Describe(rc)}";
                SetState(ConnectionState.Faulted, msg);
                return new() { Success = false, ErrorMessage = msg };
            }

            // File transfer is a blocking SDK call — give it at least a 60s window regardless of ReadTimeout config.
            var transferTimeout = Math.Max((int)config.ReadTimeout.TotalMilliseconds, 60_000);
            _api.SetOvertime(_handle, transferTimeout);
            SetState(ConnectionState.Connected);
            _logger.LogInformation("GSKRM transfer handle opened (host={Host}, handle={Handle})", config.Host, _handle);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            SetState(ConnectionState.Faulted, ex.Message);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
        finally { _lock.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_state == ConnectionState.Disconnected) return;
            if (_handle > 0) _api.CloseInstance(_handle);
            _handle = 0;
            SetState(ConnectionState.Disconnected);
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_state != ConnectionState.Connected) return false;
        await _lock.WaitAsync(ct);
        try { return _api.GetConnectState(_handle, out var ok) == GskrmErrorCodes.Ok && ok; }
        catch { return false; }
        finally { _lock.Release(); }
    }

    public Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
        => throw new NotSupportedException("GSKRM transfer driver is file-only — use GskrmDriver for tag reads");
    public Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
        => throw new NotSupportedException("GSKRM transfer driver is file-only — use GskrmDriver for tag reads");
    public Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
        => Task.FromResult<WriteResult>(new() { Success = false, ErrorMessage = "GSKRM transfer driver is file-only" });

    public async Task<IReadOnlyList<ProgramFileEntry>> BrowseFilesAsync(string? path = null, CancellationToken ct = default)
    {
        EnsureConnected();
        await _lock.WaitAsync(ct);
        try
        {
            int rc = _api.GetCNCFileList(_handle, out var list);
            if (rc != GskrmErrorCodes.Ok) return [];
            return list.Select(entry => new ProgramFileEntry
            {
                Path = "/" + entry.Name,
                Name = entry.Name,
                IsDirectory = false,
                SizeBytes = entry.SizeBytes,
                ModifiedAt = entry.ModifiedAt,
                CanDownload = true,
                CanUpload = false,
                HasChildren = false,
                Comment = entry.Attribute
            }).ToArray();
        }
        finally { _lock.Release(); }
    }

    // IAddressSpaceBrowser — GSKRM file transfer driver exposes the CNC file list as a flat variable namespace
    // so ProgramTransferController's /files + /browse gates (which require IAddressSpaceBrowser) work uniformly.
    public async Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        EnsureConnected();
        await _lock.WaitAsync(ct);
        try
        {
            int rc = _api.GetCNCFileList(_handle, out var list);
            if (rc != GskrmErrorCodes.Ok) return [];
            return list.Select(entry => new AddressNode
            {
                Path = "/" + entry.Name,
                DisplayName = entry.SizeBytes > 0 ? $"{entry.Name} ({entry.SizeBytes}B)" : entry.Name,
                NodeType = AddressNodeType.Variable,
                DataType = DataType.String,
                IsReadable = true,
                IsWritable = false,
            }).ToArray();
        }
        finally { _lock.Release(); }
    }

    public async Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var nodes = await BrowseAsync(null, ct);
        var sb = new StringBuilder();
        sb.AppendLine("Path,DisplayName,DataType,Readable,Writable");
        foreach (var node in nodes)
            sb.AppendLine($"{node.Path},{node.DisplayName},{node.DataType},{node.IsReadable},{node.IsWritable}");
        return new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    public async Task<TransferProgressResult> UploadProgramAsync(
        Stream source, NCProgramMetadata metadata, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var transferId = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();

        var tempPath = Path.Combine(Path.GetTempPath(), $"{TempTransferPrefix}{transferId}.nc");
        try
        {
            // GSKRM_SendCNCFile is path-based — materialize the inbound stream to disk first.
            long total = metadata.FileSize ?? (source.CanSeek ? source.Length : 0);
            long bytes = 0;
            using (var sha = SHA256.Create())
            await using (var tmp = File.Create(tempPath))
            {
                var buf = new byte[81920]; int n;
                while ((n = await source.ReadAsync(buf, ct)) > 0)
                {
                    await tmp.WriteAsync(buf.AsMemory(0, n), ct);
                    sha.TransformBlock(buf, 0, n, null, 0);
                    bytes += n;
                    progress?.Report(new() { BytesTransferred = bytes, TotalBytes = total });
                }
                sha.TransformFinalBlock([], 0, 0);

                var remoteName = !string.IsNullOrWhiteSpace(metadata.FileName)
                    ? metadata.FileName
                    : throw new InvalidOperationException("GSKRM upload requires NCProgramMetadata.FileName (target file name on CNC)");
                await _lock.WaitAsync(ct);
                int rc;
                try { rc = _api.SendCNCFile(_handle, tempPath, remoteName, new Progress<long>()); }
                finally { _lock.Release(); }

                if (rc != GskrmErrorCodes.Ok)
                    return Fail(transferId, sw, bytes, $"GSKRM_SendCNCFile failed: {GskrmErrorCodes.Describe(rc)}");

                sw.Stop();
                return new()
                {
                    Success = true, TransferId = transferId,
                    BytesTransferred = bytes, Duration = sw.Elapsed,
                    Checksum = Convert.ToHexString(sha.Hash!).ToLowerInvariant()
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(transferId, sw, 0, ex.Message);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public async Task<TransferProgressResult> DownloadProgramAsync(
        string remotePath, Stream destination, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var transferId = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        var tempPath = Path.Combine(Path.GetTempPath(), $"{TempTransferPrefix}{transferId}.nc");

        try
        {
            var remoteName = remotePath.TrimStart('/', '\\');
            await _lock.WaitAsync(ct);
            int rc;
            try { rc = _api.ReceiveCNCFile(_handle, remoteName, tempPath, new Progress<long>()); }
            finally { _lock.Release(); }

            if (rc != GskrmErrorCodes.Ok)
                return Fail(transferId, sw, 0, $"GSKRM_ReceiveCNCFile failed: {GskrmErrorCodes.Describe(rc)}");
            if (!File.Exists(tempPath))
                return Fail(transferId, sw, 0, $"Download reported success but '{tempPath}' not written");

            long total = new FileInfo(tempPath).Length;
            long bytes = 0;
            using var sha = SHA256.Create();
            await using (var src = File.OpenRead(tempPath))
            {
                var buf = new byte[81920]; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await destination.WriteAsync(buf.AsMemory(0, n), ct);
                    sha.TransformBlock(buf, 0, n, null, 0);
                    bytes += n;
                    progress?.Report(new() { BytesTransferred = bytes, TotalBytes = total });
                }
                sha.TransformFinalBlock([], 0, 0);
            }
            sw.Stop();
            return new()
            {
                Success = true, TransferId = transferId,
                BytesTransferred = bytes, Duration = sw.Elapsed,
                Checksum = Convert.ToHexString(sha.Hash!).ToLowerInvariant()
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(transferId, sw, 0, ex.Message);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public Task<TransferProgressResult> ResumeUploadAsync(
        string transferId, string remotePath, Stream source, long offset,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
        => Task.FromResult(new TransferProgressResult
        {
            Success = false, TransferId = transferId, BytesTransferred = offset,
            Duration = TimeSpan.Zero,
            ErrorMessage = "GSKRM transfer driver does not support resume"
        });

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _lock.Dispose();
    }

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected)
            throw new InvalidOperationException("GSKRM transfer driver is not connected.");
    }

    private void SetState(ConnectionState next, string? reason = null)
    {
        var old = _state; if (old == next) return;
        _state = next;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs { OldState = old, NewState = next, Reason = reason });
    }

    private static TransferProgressResult Fail(string id, Stopwatch sw, long bytes, string error)
    {
        sw.Stop();
        return new() { Success = false, TransferId = id, BytesTransferred = bytes, Duration = sw.Elapsed, ErrorMessage = error };
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
