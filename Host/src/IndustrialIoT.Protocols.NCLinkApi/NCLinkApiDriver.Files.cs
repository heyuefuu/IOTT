namespace IndustrialIoT.Protocols.NCLinkApi;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// 文件浏览与 G 代码传输 — 严格对照手册：
///   8.1.17 get_keys → 获取目录下文件列表
///   8.1.18 get_attributes → 获取文件大小/修改时间
///   8.1.19 add → 创建文件夹
///   8.1.20 delete → 删除文件夹
///   5.2.1  GET /v1/{device_id}/file/?key=... → 下载 G 代码
///   5.2.2  POST /v1/{device_id}/file/ multipart → 上传 G 代码
/// </summary>
public sealed partial class NCLinkApiDriver : IProgramFileBrowser, INCProgramTransfer
{
    public bool SupportsResume => false;  // 手册未规定续传协议，明确不支持

    public async Task<IReadOnlyList<ProgramFileEntry>> BrowseFilesAsync(
        string? path = null, CancellationToken ct = default)
    {
        EnsureConnected();
        // 手册 8.1.17：get_keys 列出 /MACHINE/CONTROLLER/FILE 下文件，无目录概念，path 参数忽略
        var resp = await _client!.GetKeysAsync(_deviceId, NCLinkApiPaths.File, ct).ConfigureAwait(false);
        if (!resp.IsSuccess)
            throw new NCLinkApiException(resp.StatusCode, resp.Status, "get_keys FILE");

        var keys = ExtractFileKeys(resp.Value);
        if (keys.Count == 0) return [];

        // 进一步用 get_attributes 拿大小和时间（手册 8.1.18 支持批量 key）
        var attrs = await GetAttributesSafeAsync(keys, ct).ConfigureAwait(false);

        var entries = new List<ProgramFileEntry>(keys.Count);
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var attr = i < attrs.Count ? attrs[i] : null;
            var isDir = attr?.Type?.Equals("dir", StringComparison.OrdinalIgnoreCase) == true;
            entries.Add(new ProgramFileEntry
            {
                Path = key,
                Name = key.Contains('/') ? key[(key.LastIndexOf('/') + 1)..] : key,
                IsDirectory = isDir,
                SizeBytes = attr?.Size,
                ModifiedAt = attr?.ChangeTime,
                CanDownload = !isDir,
                CanUpload = isDir,
                HasChildren = isDir,
            });
        }

        // 过滤：如果调用方给了 path 当作目录前缀过滤
        if (!string.IsNullOrWhiteSpace(path))
        {
            var prefix = path.TrimStart('/');
            entries = entries
                .Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<TransferProgressResult> UploadProgramAsync(
        Stream source, NCProgramMetadata metadata,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var tid = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        var key = BuildRemoteKey(metadata.RemotePath, metadata.FileName);

        try
        {
            var total = metadata.FileSize ?? (source.CanSeek ? source.Length : 0);
            _logger.LogInformation("NC-Link API upload: key={Key} size={Size}", key, total);

            // 先算 checksum（StreamContent 上传时会 dispose 底层流，事后无法 rewind）
            string? checksum = null;
            if (source.CanSeek)
            {
                var startPos = source.Position;
                source.Position = 0;
                var hash = await SHA256.HashDataAsync(source, ct).ConfigureAwait(false);
                checksum = Convert.ToHexString(hash).ToLowerInvariant();
                source.Position = startPos;
            }

            progress?.Report(new() { BytesTransferred = 0, TotalBytes = total });
            await _client!.UploadFileAsync(_deviceId, key, source, metadata.FileName, ct: ct)
                .ConfigureAwait(false);

            var transferred = total > 0 ? total : (source.CanSeek ? source.Length : 0);
            progress?.Report(new() { BytesTransferred = transferred, TotalBytes = total });

            sw.Stop();
            return new()
            {
                Success = true, TransferId = tid,
                BytesTransferred = transferred, Duration = sw.Elapsed, Checksum = checksum,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogError(ex, "NC-Link API upload failed: {Key}", key);
            return new()
            {
                Success = false, TransferId = tid,
                Duration = sw.Elapsed, ErrorMessage = ex.Message,
            };
        }
    }

    public async Task<TransferProgressResult> DownloadProgramAsync(
        string remotePath, Stream destination,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var tid = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("NC-Link API download: key={Key}", remotePath);

            // 先取一次 attributes 拿到大小用于进度
            long? totalSize = null;
            try
            {
                var attrs = await GetAttributesSafeAsync([remotePath], ct).ConfigureAwait(false);
                totalSize = attrs.FirstOrDefault()?.Size;
            }
            catch { /* 进度尺寸缺失不影响下载 */ }

            var bytes = await _client!.DownloadFileAsync(_deviceId, remotePath, ct: ct)
                .ConfigureAwait(false);
            await destination.WriteAsync(bytes.AsMemory(0, bytes.Length), ct).ConfigureAwait(false);
            progress?.Report(new()
            {
                BytesTransferred = bytes.Length,
                TotalBytes = totalSize ?? bytes.Length,
            });

            sw.Stop();
            return new()
            {
                Success = true, TransferId = tid,
                BytesTransferred = bytes.Length, Duration = sw.Elapsed,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogError(ex, "NC-Link API download failed: {Key}", remotePath);
            return new()
            {
                Success = false, TransferId = tid,
                Duration = sw.Elapsed, ErrorMessage = ex.Message,
            };
        }
    }

    public Task<TransferProgressResult> ResumeUploadAsync(
        string transferId, string remotePath, Stream source, long offset,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        // 手册未规定续传，明确拒绝以免假装成功
        return Task.FromResult(new TransferProgressResult
        {
            Success = false, TransferId = transferId,
            ErrorMessage = "NC-Link API 不支持断点续传（手册 5.2.2 仅定义 multipart 单次提交）",
        });
    }

    // ── 文件夹增删（手册 8.1.19/8.1.20） ──────────────────────────────────

    public async Task<bool> CreateDirectoryAsync(string dirKey, CancellationToken ct = default)
    {
        EnsureConnected();
        var resp = await _client!.AddAsync(_deviceId, NCLinkApiPaths.File,
            key: JsonValue.Create(dirKey), ct: ct).ConfigureAwait(false);
        return resp.IsSuccess;
    }

    public async Task<bool> DeleteDirectoryAsync(string dirKey, CancellationToken ct = default)
    {
        EnsureConnected();
        var resp = await _client!.DeleteAsync(_deviceId, NCLinkApiPaths.File,
            key: JsonValue.Create(dirKey), ct: ct).ConfigureAwait(false);
        return resp.IsSuccess;
    }

    // ── 辅助 ───────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<FileAttribute?>> GetAttributesSafeAsync(
        IReadOnlyList<string> keys, CancellationToken ct)
    {
        try
        {
            var keyArr = new JsonArray();
            foreach (var k in keys) keyArr.Add(k);

            var resp = await _client!.GetAttributesAsync(_deviceId, NCLinkApiPaths.File,
                keyArr, ct).ConfigureAwait(false);
            if (!resp.IsSuccess) return new FileAttribute?[keys.Count];

            // value 外层 = items（1 个），内层 = key 数组，按 key 顺序
            var outer = resp.Value as JsonArray;
            var inner = outer?.FirstOrDefault() as JsonArray;
            if (inner is null) return new FileAttribute?[keys.Count];

            var result = new List<FileAttribute?>(keys.Count);
            for (var i = 0; i < keys.Count; i++)
            {
                var attrNode = i < inner.Count ? inner[i] as JsonObject : null;
                result.Add(attrNode is null ? null : new FileAttribute
                {
                    Type = attrNode["type"]?.GetValue<string>(),
                    Size = attrNode["size"]?.GetValue<long>(),
                    ChangeTime = ParseChangeTime(attrNode["changeTime"]?.GetValue<string>()),
                });
            }
            return result;
        }
        catch
        {
            return new FileAttribute?[keys.Count];
        }
    }

    private static IReadOnlyList<string> ExtractFileKeys(JsonNode? respValue)
    {
        // 手册 8.1.17 响应：value = [[ "O0001", "OS_TOOL", ... ]]
        if (respValue is not JsonArray outer || outer.Count == 0) return [];
        if (outer[0] is not JsonArray inner) return [];
        var keys = new List<string>(inner.Count);
        foreach (var k in inner)
        {
            if (k is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                keys.Add(s);
        }
        return keys;
    }

    private static DateTimeOffset? ParseChangeTime(string? raw)
    {
        // 手册 8.1.18：changeTime 形如 "2025/04/01 15:01:45"
        if (string.IsNullOrEmpty(raw)) return null;
        return DateTimeOffset.TryParseExact(raw, "yyyy/MM/dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var dt)
            ? dt
            : DateTimeOffset.TryParse(raw, out var dt2) ? dt2 : null;
    }

    private static string BuildRemoteKey(string remotePath, string fileName)
    {
        if (string.IsNullOrEmpty(remotePath)) return fileName;
        var dir = remotePath.Trim('/');
        return string.IsNullOrEmpty(dir) ? fileName : $"{dir}/{fileName}";
    }

    private sealed record FileAttribute
    {
        public string? Type { get; init; }
        public long? Size { get; init; }
        public DateTimeOffset? ChangeTime { get; init; }
    }
}
