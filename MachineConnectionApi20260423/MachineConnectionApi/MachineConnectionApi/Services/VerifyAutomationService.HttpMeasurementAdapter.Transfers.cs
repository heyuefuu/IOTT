using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

public sealed partial class HttpVerifyMeasurementAdapter
{
    // Keyed by a run's immutable target object, never by device ID or across historical runs. Each
    // file/metric reuses this token; editing the device between files fails closed at the Host.
    private readonly ConditionalWeakTable<VerifyMeasurementTarget, TransferSnapshotState> _transferSnapshots = new();

    private async Task<VerifyTransferObservation> TransferCoreAsync(VerifyMeasurementTarget target, EvaluationFile file,
        string directory, string remoteName, Action<double> reportFraction, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (target.VerificationConfigurationChanged) return TransferUnknown("本轮已检测到设备配置变化；停止后续文件写入。");
        if (!SafeDirectory(directory) || !SafeVerificationName(remoteName, file.Name))
            return TransferUnknown("验证路径/文件名无效；未上传。");
        var validation = await ValidateAttachmentAsync(file, ct);
        if (!validation.Valid) return TransferUnknown(validation.Detail);
        reportFraction(0);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            using var client = _clients.CreateClient("IndustrialIoT");
            // This client instance is dedicated to the potentially 200 MiB round trip. Keep a finite
            // bound, but do not inherit the short point-read timeout for two real file transfers.
            client.Timeout = TimeSpan.FromMinutes(30);
            var prefix = $"api/program-transfer/{Uri.EscapeDataString(target.Id)}/verification";
            var state = _transferSnapshots.GetValue(target, _ => new());
            await state.Gate.WaitAsync(timeout.Token);
            try
            {
                if (state.Snapshot is null)
                {
                    using var response = await client.GetAsync(prefix + "/snapshot", timeout.Token);
                    if (!response.IsSuccessStatusCode)
                        return TransferUnknown($"Host未提供验证安全配置快照（HTTP {(int)response.StatusCode}）；未上传。");
                    var snapshot = await response.Content.ReadFromJsonAsync<TransferSnapshot>(JsonOptions, timeout.Token);
                    if (snapshot is null || !MatchesFrozenTarget(target, snapshot) || !ValidHash(snapshot.Token))
                    {
                        target.VerificationConfigurationChanged = true;
                        return TransferUnknown("Host实际设备/传输配置与本轮冻结目标不符；不向变化或未知目标写入。");
                    }
                    if (!snapshot.Supported || EvaluationAutomationRules.NormalizeProtocol(snapshot.Protocol) != "FTP")
                        return TransferUnknown("实际协议尚未实现验证专用原子创建；当前支持FTP（含驱动配置的FTPS），不冒险写FOCAS内部程序号或未知协议。");
                    state.Snapshot = snapshot;
                }
            }
            finally { state.Gate.Release(); }
            var frozen = state.Snapshot!;
            reportFraction(0.05);
            var binary = Path.Combine(_attachmentDirectory, Guid.ParseExact(file.Id!, "N").ToString("N") + ".bin");
            // The same locked file handle is hashed and sent; metadata validation alone is not proof
            // of the bytes uploaded this time. Never materialize a 200 MiB byte[] or base64 body.
            await using var source = new FileStream(binary, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length != file.SizeBytes || (File.GetAttributes(binary) & FileAttributes.ReparsePoint) != 0)
                return TransferUnknown("附件在传输前已变化；没有上传。");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, timeout.Token));
            source.Position = 0;
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("true"), "allowWrites");
            form.Add(new StringContent(frozen.Token), "snapshotToken");
            form.Add(new StringContent(directory), "directory");
            form.Add(new StringContent(remoteName), "remoteName");
            form.Add(new StringContent(source.Length.ToString(CultureInfo.InvariantCulture)), "expectedBytes");
            form.Add(new StringContent(hash), "expectedSha256");
            var content = new TransferFileContent(source, fraction => reportFraction(0.1 + fraction * 0.5), timeout.Token);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "file", remoteName);
            using var request = new HttpRequestMessage(HttpMethod.Post, prefix + "/round-trip") { Content = form };
            using var result = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!result.IsSuccessStatusCode)
                return TransferUnknown($"本轮文件往返HTTP {(int)result.StatusCode}，没有可靠设备上传/回读证据；清理状态未知，不重试覆盖。");
            var observation = await result.Content.ReadFromJsonAsync<TransferResult>(JsonOptions, timeout.Token);
            if (observation is null) return TransferUnknown("Host文件往返响应为空。");
            if (observation.ConfigurationChanged)
            {
                target.VerificationConfigurationChanged = true;
                return TransferUnknown(observation.Detail);
            }
            if (!observation.HasEvidence) return new(observation.Supported, false, false, false, 0, 0, observation.Detail);
            if (!observation.Supported || observation.DeviceId != target.Id || observation.SnapshotToken != frozen.Token
                || observation.RemoteName != remoteName || observation.Protocol != frozen.Protocol
                || !SameHash(observation.SourceSha256, hash) || observation.UploadedBytes < 0 || observation.UploadedBytes > source.Length
                || !double.IsFinite(observation.UploadSeconds) || observation.UploadSeconds < 0)
                return TransferUnknown("本轮返回的设备、快照、文件、哈希或字节计时不匹配；不能作为测量证据。");
            if ((observation.Success || observation.IntegrityVerified)
                && (!observation.Success || !observation.IntegrityVerified || observation.UploadedBytes != source.Length
                    || observation.DownloadedBytes != source.Length || !SameHash(observation.ReadBackSha256, hash)
                    || observation.UploadSeconds <= 0))
                return TransferUnknown("本轮成功标记缺少完整大小、SHA256回读或有效上传计时证据。");
            reportFraction(1);
            return new(true, true, observation.Success, observation.IntegrityVerified,
                observation.UploadedBytes, observation.UploadSeconds,
                $"实际协议 {observation.Protocol}；本轮路径 {observation.RemotePath}；上传 {observation.UploadedBytes} 字节/{observation.UploadSeconds:0.######} 秒，回读 {observation.DownloadedBytes} 字节；" + observation.Detail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or UnauthorizedAccessException)
        {
            return TransferUnknown($"本轮文件往返未取得可验证响应（{ex.GetType().Name}）；不使用历史数据；设备独占测试路径清理状态须核对Host日志。");
        }
    }

    private static bool SafeVerificationName(string name, string originalName)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith("verify-", StringComparison.Ordinal)) return false;
        var extension = Path.GetExtension(name);
        return FileExtensions.Contains(extension) && string.Equals(extension, Path.GetExtension(originalName), StringComparison.OrdinalIgnoreCase)
            && name.Length == 39 + extension.Length && Guid.TryParseExact(name.AsSpan(7, 32), "N", out _);
    }

    private static bool MatchesFrozenTarget(VerifyMeasurementTarget target, TransferSnapshot snapshot)
    {
        var primary = snapshot.Primary;
        return snapshot.DeviceId == target.Id && primary is not null && primary.ExtendedProperties is not null
            && EvaluationAutomationRules.NormalizeProtocol(primary.Protocol) == EvaluationAutomationRules.NormalizeProtocol(target.Protocol)
            && primary.Host == target.Host && primary.Port == target.Port && primary.Brand == target.Brand && primary.Model == target.Model
            && (primary.Username ?? "") == (target.Username ?? "")
            && primary.ConnectTimeoutMs == target.ConnectTimeoutMs && primary.ReadTimeoutMs == target.ReadTimeoutMs
            && primary.ExtendedProperties.Count == target.ExtendedProperties.Count
            && target.ExtendedProperties.All(pair => primary.ExtendedProperties.TryGetValue(pair.Key, out var value) && value == pair.Value)
            && ((target.Transfer is null && primary.Transfer is null) || (target.Transfer is { } expected && primary.Transfer is { } actual
                && EvaluationAutomationRules.NormalizeProtocol(expected.Protocol) == EvaluationAutomationRules.NormalizeProtocol(actual.Protocol)
                && expected.Host == actual.Host && expected.Port == actual.Port && (expected.Username ?? "") == (actual.Username ?? "")
                && expected.ConnectTimeoutMs == actual.ConnectTimeoutMs && expected.ReadTimeoutMs == actual.ReadTimeoutMs
                && actual.ExtendedProperties is not null && expected.ExtendedProperties.Count == actual.ExtendedProperties.Count
                && expected.ExtendedProperties.All(pair => actual.ExtendedProperties.TryGetValue(pair.Key, out var value) && value == pair.Value)));
    }

    private static bool ValidHash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool SameHash(string a, string b) => ValidHash(a) && ValidHash(b)
        && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a), Convert.FromHexString(b));
    private static VerifyTransferObservation TransferUnknown(string detail) => new(false, false, false, false, 0, 0, detail);

    private sealed class TransferSnapshotState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public TransferSnapshot? Snapshot { get; set; }
    }
    private sealed class TransferSnapshot
    {
        public string DeviceId { get; set; } = "";
        public string Token { get; set; } = "";
        public bool Supported { get; set; }
        public string Protocol { get; set; } = "";
        public TransferPrimary? Primary { get; set; }
    }
    private sealed class TransferPrimary
    {
        public string Protocol { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string Brand { get; set; } = "";
        public string Model { get; set; } = "";
        public string? Username { get; set; }
        public int ConnectTimeoutMs { get; set; }
        public int ReadTimeoutMs { get; set; }
        public Dictionary<string, string> ExtendedProperties { get; set; } = new(StringComparer.Ordinal);
        public VerifyTransferTarget? Transfer { get; set; }
    }
    private sealed class TransferResult
    {
        public bool Supported { get; set; }
        public bool HasEvidence { get; set; }
        public bool Success { get; set; }
        public bool IntegrityVerified { get; set; }
        public bool ConfigurationChanged { get; set; }
        public string DeviceId { get; set; } = "";
        public string SnapshotToken { get; set; } = "";
        public string Protocol { get; set; } = "";
        public string RemoteName { get; set; } = "";
        public string RemotePath { get; set; } = "";
        public long UploadedBytes { get; set; }
        public long DownloadedBytes { get; set; }
        public double UploadSeconds { get; set; }
        public string SourceSha256 { get; set; } = "";
        public string ReadBackSha256 { get; set; } = "";
        public string Detail { get; set; } = "";
    }
    private sealed class TransferFileContent(Stream source, Action<double> progress, CancellationToken requestToken) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = source.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => CopyAsync(stream, requestToken);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) => CopyAsync(stream, ct);
        private async Task CopyAsync(Stream destination, CancellationToken ct)
        {
            var buffer = new byte[81920];
            long bytes = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) != 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                bytes += read;
                progress(bytes / (double)source.Length);
            }
        }
    }
}
