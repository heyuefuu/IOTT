using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

/// <summary>
/// Uses Host configuration-bound application and directory reads, not TCP probes, simulator data
/// or transfer history. File round trips are delegated to the separately implemented transfer
/// partial, which must enforce its own authorized create-only write and integrity contract.
/// </summary>
public sealed partial class HttpVerifyMeasurementAdapter : IVerifyMeasurementAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ReadTypes = new(StringComparer.Ordinal)
    {
        "Bool", "Int8", "UInt8", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Float", "Double", "String", "ByteArray",
    };
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase) { ".nc", ".ncg", ".txt", ".xml", ".prg" };
    private readonly IHttpClientFactory _clients;
    private readonly string _attachmentDirectory;
    private readonly IDeviceCollectionPointStore? _pointStore;

    public HttpVerifyMeasurementAdapter(IHttpClientFactory clients, string? attachmentDirectory = null, IDeviceCollectionPointStore? pointStore = null)
    {
        _clients = clients;
        _attachmentDirectory = attachmentDirectory ?? Path.Combine(AppContext.BaseDirectory, "App_Data", "evaluation-attachments");
        _pointStore = pointStore;
    }

    public async Task<VerifyReadObservation> ReadAsync(VerifyMeasurementTarget target, string address, string dataType, int timeoutMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var protocol = EvaluationAutomationRules.NormalizeProtocol(target.Protocol);
        if (IsSimulatedProtocol(protocol))
            return Unknown("模拟驱动只能用于开发，不是真实机床通讯证据。", protocol);
        if (protocol is "FTP" or "FTPS" or "SFTP" or "SMB" or "NFS" or "SERIAL" or "GSKRMFILETRANSFER")
            return Unknown("该文件传输协议没有应用点位读取接口；不会用TCP握手替代。", protocol);
        await target.VerificationGate.WaitAsync(ct);
        try
        {
            if (target.VerificationConfigurationChanged) return ConfigurationChanged(protocol);
            return await ReadCoreAsync(target, address, dataType, timeoutMs, protocol, ct);
        }
        finally { target.VerificationGate.Release(); }
    }

    private async Task<VerifyReadObservation> ReadCoreAsync(VerifyMeasurementTarget target, string address, string dataType,
        int timeoutMs, string protocol, CancellationToken ct)
    {
        try
        {
            var client = _clients.CreateClient("IndustrialIoT");
            var source = "评价规则点位";
            var candidates = new List<(string Address, string DataType)>();
            if (string.IsNullOrWhiteSpace(address))
            {
                source = "本轮已配置的设备采集点位";
                if (!target.ReadPointResolved)
                {
                    using var configTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    configTimeout.CancelAfter(Math.Max(timeoutMs, 3000));
                    using var config = await client.GetAsync($"api/collection-config/{Uri.EscapeDataString(target.Id)}", configTimeout.Token);
                    if (!config.IsSuccessStatusCode) return Unknown($"读取现有设备点位失败（HTTP {(int)config.StatusCode}）；未猜测点位。", protocol);
                    using var json = await JsonDocument.ParseAsync(await config.Content.ReadAsStreamAsync(configTimeout.Token), cancellationToken: configTimeout.Token);
                    if (json.RootElement.ValueKind != JsonValueKind.Array) return Unknown("设备点位响应格式无效；未猜测点位。", protocol);
                    target.ConfiguredReadPoints = ConfiguredPoints(json.RootElement, target.Id, 20);
                    // "点位配置" writes the device's saved points to the gateway Datacollection store, not
                    // to Host collection profiles. Fall back to that store so a point selected in the UI is
                    // actually used instead of reporting "no valid collection point".
                    foreach (var point in _pointStore?.FindReadableCandidates(target.Id, 20) ?? [])
                        if (!target.ConfiguredReadPoints.Contains(point)) target.ConfiguredReadPoints.Add(point);
                    target.ConfiguredReadPoints = OrderCandidates(target.ConfiguredReadPoints);
                    target.ReadPointResolved = true;
                }
                if (target.ConfiguredReadPoints is not { Count: > 0 })
                    return Unknown("未配置规则读取地址，设备也没有有效采集点位；本轮未发起应用读。", protocol);
                candidates.AddRange(target.ConfiguredReadPoints);
            }
            else
            {
                candidates.Add((address, dataType));
            }

            VerifyReadObservation? last = null;
            foreach (var candidate in candidates)
            {
                var observation = await ReadOneAsync(client, target, candidate.Address, candidate.DataType, timeoutMs, protocol, source, ct);
                if (observation.Success) return observation;
                last = observation;
                if (target.VerificationConfigurationChanged) return observation;
            }
            return last ?? Unknown("未取得任何可验证的应用读结果。", protocol);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        { return Unknown($"应用读未取得可验证响应（{ex.GetType().Name}），不能以HTTP/TCP连通代替。", protocol); }
    }

    private async Task<VerifyReadObservation> ReadOneAsync(HttpClient client, VerifyMeasurementTarget target,
        string address, string dataType, int timeoutMs, string protocol, string source, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            if (address.Length > 500 || address.Any(char.IsControl) || !ReadTypes.Contains(dataType))
                return Unknown("读取地址或Host数据类型无效；本轮未发起应用读。", protocol);

            var fingerprint = ConfigurationFingerprint(target);
            using var response = await client.PostAsJsonAsync($"api/data/{Uri.EscapeDataString(target.Id)}/read",
                new { tags = new[] { new { address, dataType } }, verification = new
                { configurationFingerprint = fingerprint, revision = target.VerificationReadRevision } }, JsonOptions, timeout.Token);
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var root = body.RootElement;
            if (!AcceptVerificationEvidence(root, target, fingerprint, "application-read", protocol, out var code))
                return target.VerificationConfigurationChanged ? ConfigurationChanged(protocol)
                    : Unknown("Host未提供匹配本轮配置、非模拟且已执行应用读的结构化证据；未评分。", protocol);
            if (!response.IsSuccessStatusCode)
            {
                // Only an explicit operation marker set inside the actual read callback is evidence.
                // A 502 or 'Read failed:' string also occurs during connection/SDK initialization.
                if (response.StatusCode == HttpStatusCode.BadGateway && code == "operation-failed")
                    return new(false, true, false, $"{source} {address}/{dataType} 本轮实际应用读失败。", protocol);
                return Unknown($"应用读未取得可靠结果（HTTP {(int)response.StatusCode}，{code}）。", protocol);
            }
            if (code != "completed" || !root.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
                return Unknown("应用读响应设备或点位列表不匹配，不能作通讯证据。", protocol);
            var values = tags.EnumerateArray().Where(tag => Text(tag, "address") == address).ToList();
            if (values.Count != 1) return Unknown("应用读响应缺少唯一的请求点位，不能作通讯证据。", protocol);
            var value = values[0];
            if (IsTrue(value, "simulated") || IsTrue(value, "isSimulated"))
                return Unknown("服务端返回模拟点位数据，不能作真实通讯证据。", protocol);
            var quality = Text(value, "quality");
            var detail = Text(value, "errorMessage");
            if (Unsupported(detail)) return Unknown($"{source} {address}/{dataType}：{detail}", protocol);
            if (quality is not ("Good" or "Bad" or "Uncertain")) return Unknown("应用读没有有效数据质量标记，不能作通讯证据。", protocol);
            var success = quality == "Good" && string.IsNullOrWhiteSpace(detail)
                && value.TryGetProperty("value", out var reading) && reading.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            if (quality == "Good" && !success) return Unknown($"{source} {address}/{dataType} 没有可靠有效读值：{detail}", protocol);
            return new(true, true, success, $"{source} {address}/{dataType} 应用读质量 {quality} {detail}".TrimEnd(), protocol);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        { return Unknown($"应用读未取得可验证响应（{ex.GetType().Name}），不能以HTTP/TCP连通代替。", protocol); }
    }

    public async Task<VerifyConnectionsObservation> MeasureConnectionsAsync(VerifyMeasurementTarget target, int count,
        string address, string dataType, int timeoutMs, CancellationToken ct)
    {
        if (count < 1) return new(false, false, 0, count, "count must be >= 1");
        using var client = _clients.CreateClient("IndustrialIoT");
        var candidates = new List<(string Address, string DataType)>();
        if (!string.IsNullOrWhiteSpace(address))
        {
            candidates.Add((address, dataType));
        }
        else
        {
            if (!target.ReadPointResolved)
            {
                using var configTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                configTimeout.CancelAfter(Math.Max(timeoutMs, 3000));
                using var config = await client.GetAsync($"api/collection-config/{Uri.EscapeDataString(target.Id)}", configTimeout.Token);
                if (!config.IsSuccessStatusCode) return new(false, false, 0, count, $"collection-config HTTP {(int)config.StatusCode}");
                using var json = await JsonDocument.ParseAsync(await config.Content.ReadAsStreamAsync(configTimeout.Token), cancellationToken: configTimeout.Token);
                if (json.RootElement.ValueKind != JsonValueKind.Array) return new(false, false, 0, count, "invalid collection-config response");
                target.ConfiguredReadPoints = ConfiguredPoints(json.RootElement, target.Id, 20);
                foreach (var point in _pointStore?.FindReadableCandidates(target.Id, 20) ?? [])
                    if (!target.ConfiguredReadPoints.Contains(point)) target.ConfiguredReadPoints.Add(point);
                target.ConfiguredReadPoints = OrderCandidates(target.ConfiguredReadPoints);
                target.ReadPointResolved = true;
            }
            if (target.ConfiguredReadPoints is not { Count: > 0 })
                return new(false, false, 0, count, "no rule address and no valid configured points");
            candidates.AddRange(target.ConfiguredReadPoints);
        }

        var protocol = EvaluationAutomationRules.NormalizeProtocol(target.Protocol);
        if (IsSimulatedProtocol(protocol))
            return new(false, false, 0, count, "Simulated drivers are not verification evidence.");
        if (protocol is "FTP" or "FTPS" or "SFTP" or "SMB" or "NFS" or "SERIAL" or "GSKRMFILETRANSFER")
            return new(false, false, 0, count, "This protocol has no application point read interface.");

        (string Address, string DataType)? selected = null;
        string? lastDetail = null;
        await target.VerificationGate.WaitAsync(ct);
        try
        {
            if (target.VerificationConfigurationChanged) return new(false, false, 0, count, "Configuration changed during this run.");
            foreach (var candidate in candidates)
            {
                var probe = await ReadOneAsync(client, target, candidate.Address, candidate.DataType, timeoutMs, protocol, "independent-connection-probe", ct);
                if (probe.Success) { selected = candidate; break; }
                lastDetail = probe.Detail;
                if (target.VerificationConfigurationChanged) return new(false, false, 0, count, "Configuration changed during probe.");
            }
        }
        finally { target.VerificationGate.Release(); }
        if (selected is null) return new(true, true, 0, count, lastDetail ?? "no readable point");

        var results = await Task.WhenAll(Enumerable.Range(0, count).Select(async index =>
        {
            using var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 1,
                PooledConnectionLifetime = TimeSpan.Zero,
                ConnectTimeout = TimeSpan.FromMilliseconds(Math.Max(1000, timeoutMs)),
            };
            using var http = new HttpClient(handler)
            {
                BaseAddress = client.BaseAddress,
                Timeout = TimeSpan.FromMilliseconds(Math.Max(3000, timeoutMs * 3)),
            };
            var observation = await ReadOneAsync(http, target, selected.Value.Address, selected.Value.DataType,
                timeoutMs, protocol, $"independent-connection-{index + 1}", ct);
            return observation.Success;
        }));
        var successes = results.Count(x => x);
        return new(true, true, successes, count,
            $"{successes}/{count} independent connections read {selected.Value.Address}/{selected.Value.DataType} with quality Good");
    }

    public async Task<VerifyTransferSnapshot> GetTransferSnapshotAsync(VerifyMeasurementTarget target, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var client = _clients.CreateClient("IndustrialIoT");
            using var response = await client.GetAsync(
                $"api/program-transfer/{Uri.EscapeDataString(target.Id)}/verification/snapshot", timeout.Token);
            if (!response.IsSuccessStatusCode) return new(false, "", $"snapshot HTTP {(int)response.StatusCode}");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var root = json.RootElement;
            var supported = root.TryGetProperty("supported", out var s) && s.ValueKind == JsonValueKind.True;
            var protocol = root.TryGetProperty("protocol", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            return new(supported, protocol, supported ? $"snapshot protocol {protocol}" : "snapshot not supported");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        { return new(false, "", $"snapshot failed: {ex.GetType().Name}"); }
    }

    private static List<(string Address, string DataType)> OrderCandidates(List<(string Address, string DataType)> points) =>
        points.Select((point, index) => (point, index))
            .OrderBy(x => Preferred(x.point.Address) ? 0 : 1)
            .ThenBy(x => x.index)
            .Select(x => x.point)
            .ToList();

    private static bool Preferred(string address)
    {
        if (address.StartsWith("/Realtime.", StringComparison.OrdinalIgnoreCase)) return true;
        var trimmed = address.TrimStart('/');
        foreach (var prefix in new[] { "HR", "IR", "C", "DI" })
            if (trimmed.Length > prefix.Length && trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && char.IsDigit(trimmed[prefix.Length])) return true;
        return false;
    }
    public async Task<VerifyReadObservation> ReadTransferDirectoryAsync(VerifyMeasurementTarget target, string directory, int timeoutMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var protocol = EvaluationAutomationRules.NormalizeProtocol(target.ResolvedTransferProtocol ?? target.Transfer?.Protocol ?? target.Protocol);
        if (IsSimulatedProtocol(protocol)) return Unknown("模拟驱动目录不是实际传输协议证据。", protocol);
        if (!SafeDirectory(directory)) return Unknown("测试目录无效，没有访问设备。", protocol);
        if (protocol == "NFS")
            return Unknown("NFS 目录可能来自客户端缓存，不计作本轮通讯证据；请使用文件上传及无缓存回读验证。", protocol);
        if (protocol is not ("FTP" or "SMB" or "NC-Link" or "FOCAS"))
            return Unknown("该驱动目录接口尚不能证明真实协议读取；静态地址树不计作证据。", protocol);
        if (protocol == "FOCAS" && !string.Equals(directory.Replace('\\', '/').TrimEnd('/'), "/Programs", StringComparison.OrdinalIgnoreCase))
            return Unknown("FOCAS只有显式/Programs目录可用于验证，静态根节点不作通讯证据。", protocol);
        await target.VerificationGate.WaitAsync(ct);
        try
        {
            if (target.VerificationConfigurationChanged) return ConfigurationChanged(protocol);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            var client = _clients.CreateClient("IndustrialIoT");
            var fingerprint = ConfigurationFingerprint(target);
            // A read-only POST carries configuration constraints without leaking them into URL logs.
            // Legacy capabilities + files GETs are not atomic and cannot bind the actual driver.
            using var response = await client.PostAsJsonAsync($"api/data/{Uri.EscapeDataString(target.Id)}/verification-directory",
                new { directory, verification = new { configurationFingerprint = fingerprint, revision = target.VerificationDirectoryRevision } },
                JsonOptions, timeout.Token);
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            if (!AcceptVerificationEvidence(body.RootElement, target, fingerprint, "directory-read", protocol, out var code))
                return target.VerificationConfigurationChanged ? ConfigurationChanged(protocol)
                    : Unknown("目录API没有匹配本轮配置、非模拟且实际执行的结构化证据；未评分。", protocol);
            if (!response.IsSuccessStatusCode || code != "completed")
                return Unknown($"本轮目录读取没有可靠结果（HTTP {(int)response.StatusCode}，{code}）。", protocol);
            if (!body.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                return Unknown("本轮文件目录响应格式无效。", protocol);
            if (files.GetArrayLength() == 0 && protocol is "FOCAS" or "SMB")
                return Unknown("该Host驱动将目录错误也返回空数组，空响应不能证明通讯成功。", protocol);
            return new(true, true, true, $"本轮 {protocol} 文件目录 {directory} 读取完成，返回 {files.GetArrayLength()} 项（配置已核验，非历史记录、非上传验证）。", protocol);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        { return Unknown($"传输目录未取得可验证响应（{ex.GetType().Name}）；配置不计作实测。", protocol); }
        finally { target.VerificationGate.Release(); }
    }

    public async Task<VerifyAttachmentValidation> ValidateAttachmentAsync(EvaluationFile file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(file.Id, "N", out var parsed) || file.SizeBytes is not (> 0 and <= 209715200)
            || string.IsNullOrWhiteSpace(file.Name) || file.Name.Length > 255 || file.Name != Path.GetFileName(file.Name.Replace('\\', '/'))
            || file.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !FileExtensions.Contains(Path.GetExtension(file.Name)))
            return new(false, "附件ID、名称、扩展名或声明大小无效；默认文件名不代表真实附件。");
        var id = parsed.ToString("N");
        var binary = Path.Combine(_attachmentDirectory, id + ".bin");
        var metadata = Path.Combine(_attachmentDirectory, id + ".json");
        try
        {
            if (!File.Exists(binary) || !File.Exists(metadata)) return new(false, "本机网关附件二进制或元数据不存在；没有查询历史记录替代。");
            if ((File.GetAttributes(binary) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(metadata) & FileAttributes.ReparsePoint) != 0)
                return new(false, "附件不是普通本地存储文件。");
            var stored = JsonSerializer.Deserialize<EvaluationFile>(await File.ReadAllTextAsync(metadata, ct), JsonOptions);
            await using var stream = new FileStream(binary, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stored is null || stored.Id != id || stored.Name != file.Name || stored.SizeBytes != file.SizeBytes || stream.Length != file.SizeBytes)
                return new(false, "本机网关附件实际大小、元数据与本次配置不一致。");
            return new(true, $"本机网关真实附件已核对，{stream.Length} 字节。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new(false, $"本机网关附件不可用（{ex.GetType().Name}）。"); }
    }

    public Task<VerifyTransferObservation> TransferAsync(VerifyMeasurementTarget target, EvaluationFile file, string directory,
        string remoteName, Action<double> reportFraction, CancellationToken ct) =>
        TransferCoreAsync(target, file, directory, remoteName, reportFraction, ct);

    private static List<(string Address, string DataType)> ConfiguredPoints(JsonElement profiles, string deviceId, int max)
    {
        var points = new List<(string Address, string DataType)>();
        foreach (var profile in profiles.EnumerateArray())
        {
            if (Text(profile, "deviceId") != deviceId || !profile.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array) continue;
            foreach (var group in groups.EnumerateArray())
            {
                if (!group.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) continue;
                foreach (var tag in tags.EnumerateArray())
                {
                    var address = Text(tag, "address");
                    var dataType = Text(tag, "dataType");
                    if (string.IsNullOrWhiteSpace(address) || address.Length > 500 || address.Any(char.IsControl) || !ReadTypes.Contains(dataType)) continue;
                    if (!points.Contains((address, dataType))) points.Add((address, dataType));
                    if (points.Count >= max) return points;
                }
            }
        }
        return points;
    }
    // Mirrors Host VerificationConfiguration.Fingerprint v1. Only the digest crosses this boundary;
    // the Host obtains credentials itself and returns a keyed revision covering secret changes too.
    internal static string ConfigurationFingerprint(VerifyMeasurementTarget target) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            "verification-config-v1", target.Id, target.Protocol, target.Brand, target.Model,
            target.Host, target.Port, target.Username, target.ConnectTimeoutMs, target.ReadTimeoutMs, Properties(target.ExtendedProperties),
            target.Transfer is not { } transfer ? null : new object?[]
            {
                transfer.Protocol, transfer.Host, transfer.Port, transfer.Username, transfer.ConnectTimeoutMs,
                transfer.ReadTimeoutMs, Properties(transfer.ExtendedProperties),
            },
        }, JsonOptions)));

    private static string[][] Properties(Dictionary<string, string> properties) => properties
        .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new[] { pair.Key, pair.Value }).ToArray();

    private static bool AcceptVerificationEvidence(JsonElement root, VerifyMeasurementTarget target, string fingerprint,
        string operation, string protocol, out string code)
    {
        code = "unverified";
        if (Text(root, "deviceId") != target.Id || !root.TryGetProperty("evidence", out var evidence)
            || evidence.ValueKind != JsonValueKind.Object || !evidence.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 || Text(evidence, "operation") != operation) return false;
        code = Text(evidence, "code");
        if (code == "configuration-changed")
        {
            target.VerificationConfigurationChanged = true;
            return false;
        }
        if (!IsTrue(evidence, "configurationMatched") || Text(evidence, "configurationFingerprint") != fingerprint
            || !evidence.TryGetProperty("simulated", out var simulated) || simulated.ValueKind != JsonValueKind.False
            || IsTrue(root, "simulated") || IsSimulatedProtocol(Text(evidence, "protocol"))
            || EvaluationAutomationRules.NormalizeProtocol(Text(evidence, "protocol")) != protocol) return false;
        var revision = Text(evidence, "revision");
        if (revision.Length != 64 || !revision.All(Uri.IsHexDigit)) return false;
        var previous = operation == "application-read" ? target.VerificationReadRevision : target.VerificationDirectoryRevision;
        if (previous is not null && previous != revision)
        {
            target.VerificationConfigurationChanged = true;
            return false;
        }
        if (operation == "application-read") target.VerificationReadRevision = revision;
        else target.VerificationDirectoryRevision = revision;
        return IsTrue(evidence, "operationAttempted");
    }

    private static bool IsSimulatedProtocol(string protocol) => EvaluationAutomationRules.NormalizeProtocol(protocol) is
        "SIMULATOR" or "SIMULATED" or "SIMULATION";
    private static VerifyReadObservation ConfigurationChanged(string protocol) =>
        Unknown("Host确认本轮设备/协议/连接配置已经改变；本轮未评分，不再继续读取或混用目标。", protocol);

    private static bool SafeDirectory(string directory) => !string.IsNullOrWhiteSpace(directory) && directory.Length <= 1024
        && !directory.Any(char.IsControl) && !directory.Replace('\\', '/').Split('/').Any(part => part is "." or "..");
    private static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool IsTrue(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool Unsupported(string text) => text.Contains("not support", StringComparison.OrdinalIgnoreCase)
        || text.Contains("unsupported", StringComparison.OrdinalIgnoreCase) || text.Contains("不支持", StringComparison.Ordinal);
    private static VerifyReadObservation Unknown(string detail, string? protocol = null) => new(false, false, false, detail, protocol);
}
