namespace IndustrialIoT.Protocols.NCLink;

using System.Diagnostics;
using System.Security.Cryptography;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// NC-Link 程序传输 — 通过 MQTT Set/Request 分块上传/下载 NC 程序。
/// 协议: 文件内容 base64 编码，通过 FILE 类型 DataItem 操作。
/// </summary>
public sealed partial class NCLinkDriver : INCProgramTransfer
{
    public bool SupportsResume => true;
    private const int DefaultChunkSize = 32 * 1024; // 32KB

    public async Task<TransferProgressResult> UploadProgramAsync(
        Stream source, NCProgramMetadata metadata,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var tid = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        var fid = ResolveFileDataItemId();
        var key = BuildRemoteKey(metadata.RemotePath, metadata.FileName);
        var total = metadata.FileSize ?? source.Length;
        var chunkSize = GetChunkSize();

        try
        {
            _logger.LogInformation("NC-Link upload started: {Key} ({Total} bytes, chunk={Chunk})",
                key, total, chunkSize);

            long offset = 0;
            var buf = new byte[chunkSize];
            int read;

            while ((read = await source.ReadAsync(buf.AsMemory(0, chunkSize), ct)) > 0)
            {
                var chunk = read == chunkSize ? buf : buf[..read];
                var mid = NCLinkProtocol.NextMessageId();
                var payload = NCLinkProtocol.BuildFileUploadChunkRequest(
                    mid, fid, key, offset, read, chunk);

                var resp = await PublishAndWaitAsync(
                    NCLinkProtocol.SetRequestTopic(_deviceGuid), payload, mid, ct);
                NCLinkProtocol.ThrowIfError(resp);

                offset += read;
                progress?.Report(new() { BytesTransferred = offset, TotalBytes = total });
            }

            sw.Stop();
            string? checksum = null;
            if (source.CanSeek)
            {
                source.Position = 0;
                var hash = await SHA256.HashDataAsync(source, ct);
                checksum = Convert.ToHexString(hash).ToLowerInvariant();
            }

            _logger.LogInformation("NC-Link upload OK: {Key} ({Bytes}B) in {D}", key, offset, sw.Elapsed);
            return new() { Success = true, TransferId = tid, BytesTransferred = offset,
                Duration = sw.Elapsed, Checksum = checksum };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogError(ex, "NC-Link upload failed: {Key}", key);
            return new() { Success = false, TransferId = tid,
                Duration = sw.Elapsed, ErrorMessage = ex.Message };
        }
    }

    public async Task<TransferProgressResult> DownloadProgramAsync(
        string remotePath, Stream destination,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var tid = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        var fid = ResolveFileDataItemId();
        var chunkSize = GetChunkSize();

        try
        {
            _logger.LogInformation("NC-Link download started: {Path}", remotePath);
            long offset = 0;
            long? totalSize = null;

            while (true)
            {
                var mid = NCLinkProtocol.NextMessageId();
                var payload = NCLinkProtocol.BuildFileDownloadChunkRequest(
                    mid, fid, remotePath, offset, chunkSize);

                var resp = await PublishAndWaitAsync(
                    NCLinkProtocol.SetRequestTopic(_deviceGuid), payload, mid, ct);
                NCLinkProtocol.ThrowIfError(resp);

                var chunk = NCLinkProtocol.ParseFileChunkResponse(resp.Raw);
                if (chunk is null || string.IsNullOrEmpty(chunk.Data))
                    break;

                var data = Convert.FromBase64String(chunk.Data);
                await destination.WriteAsync(data, ct);
                offset += data.Length;

                if (chunk.FileTotalSize.HasValue)
                    totalSize = chunk.FileTotalSize.Value;

                progress?.Report(new() { BytesTransferred = offset,
                    TotalBytes = totalSize ?? offset });

                // Last chunk: received less data than requested
                if (data.Length < chunkSize)
                    break;
            }

            sw.Stop();
            _logger.LogInformation("NC-Link download OK: {Path} ({Bytes}B) in {D}",
                remotePath, offset, sw.Elapsed);
            return new() { Success = true, TransferId = tid,
                BytesTransferred = offset, Duration = sw.Elapsed };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogError(ex, "NC-Link download failed: {Path}", remotePath);
            return new() { Success = false, TransferId = tid,
                Duration = sw.Elapsed, ErrorMessage = ex.Message };
        }
    }

    public async Task<TransferProgressResult> ResumeUploadAsync(
        string transferId, string remotePath, Stream source, long offset,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var sw = Stopwatch.StartNew();
        var fid = ResolveFileDataItemId();
        var total = source.CanSeek ? source.Length : offset;
        var chunkSize = GetChunkSize();

        try
        {
            if (source.CanSeek && source.Position != offset)
                source.Seek(offset, SeekOrigin.Begin);

            _logger.LogInformation("NC-Link resume upload: {Path} from offset {Offset}", remotePath, offset);

            long cur = offset;
            var buf = new byte[chunkSize];
            int read;

            while ((read = await source.ReadAsync(buf.AsMemory(0, chunkSize), ct)) > 0)
            {
                var chunk = read == chunkSize ? buf : buf[..read];
                var mid = NCLinkProtocol.NextMessageId();
                var payload = NCLinkProtocol.BuildFileUploadChunkRequest(
                    mid, fid, remotePath, cur, read, chunk);

                var resp = await PublishAndWaitAsync(
                    NCLinkProtocol.SetRequestTopic(_deviceGuid), payload, mid, ct);
                NCLinkProtocol.ThrowIfError(resp);

                cur += read;
                progress?.Report(new() { BytesTransferred = cur, TotalBytes = total });
            }

            sw.Stop();
            _logger.LogInformation("NC-Link resume OK: {Path} offset {Off}→{Cur}", remotePath, offset, cur);
            return new() { Success = true, TransferId = transferId,
                BytesTransferred = cur, Duration = sw.Elapsed };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogError(ex, "NC-Link resume upload failed: {Path}", remotePath);
            return new() { Success = false, TransferId = transferId,
                BytesTransferred = offset, Duration = sw.Elapsed, ErrorMessage = ex.Message };
        }
    }

    // ── 文件传输辅助 ──────────────────────────────────────────────────

    /// <summary>
    /// 从设备模型中发现 FILE 类型的 DataItem ID。
    /// 查找优先级: Probe模型 → ExtendedProperties → 默认值。
    /// </summary>
    private string ResolveFileDataItemId()
    {
        if (_deviceModel is not null)
        {
            var fi = _deviceModel.DataItems.FirstOrDefault(d =>
                d.Type.Equals("FILE", StringComparison.OrdinalIgnoreCase) ||
                d.Type.Equals("HASH", StringComparison.OrdinalIgnoreCase));
            if (fi is not null) return fi.Id;
        }

        if (_config?.ExtendedProperties.TryGetValue("FileDataItemId", out var id) == true
            && !string.IsNullOrEmpty(id))
            return id;

        return "01035408"; // 国标默认 G代码文件 DataItem ID
    }

    private int GetChunkSize()
    {
        if (_config?.ExtendedProperties.TryGetValue("TransferChunkSize", out var s) == true
            && int.TryParse(s, out var v) && v > 0)
            return Math.Min(v, 64 * 1024);
        return DefaultChunkSize;
    }

    private static string BuildRemoteKey(string remotePath, string fileName)
    {
        if (string.IsNullOrEmpty(remotePath)) return fileName;
        return $"{remotePath.TrimEnd('/')}/{fileName}";
    }
}
