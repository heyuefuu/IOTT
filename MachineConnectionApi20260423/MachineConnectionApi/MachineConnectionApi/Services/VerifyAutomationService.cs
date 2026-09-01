using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

public interface IVerifyAutomationService
{
    Task<VerifyRunResponse> RunAsync(VerifyRunRequest request, CancellationToken ct);
}

/// <summary>
/// 验证自动化（对应《5.docx》7 项软件指标）。原则与设计方案一致：实测为准、只存原始值不打分；
/// 达标/未达标按指标库（/api/metrics）中同 Code 指标维护的阈值标注，未配置阈值时退回内置默认判据。
/// </summary>
public sealed class VerifyAutomationService : IVerifyAutomationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICsConnectivityService _csService;
    private readonly IMetricStore _metricStore;
    private readonly IConfiguration _configuration;
    private readonly ILogger<VerifyAutomationService> _logger;

    public VerifyAutomationService(
        IHttpClientFactory httpClientFactory,
        ICsConnectivityService csService,
        IMetricStore metricStore,
        IConfiguration configuration,
        ILogger<VerifyAutomationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _csService = csService;
        _metricStore = metricStore;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<VerifyRunResponse> RunAsync(VerifyRunRequest request, CancellationToken ct)
    {
        var options = NormalizeOptions(request.Options);
        var metricIds = NormalizeMetricIds(request.MetricIds);
        var devices = await LoadDevicesAsync(ct);
        var response = new VerifyRunResponse
        {
            RunId = Guid.NewGuid().ToString("N"),
            TaskId = request.TaskId,
            TaskName = string.IsNullOrWhiteSpace(request.TaskName) ? "自动验证任务" : request.TaskName.Trim(),
            StartedAt = Now(),
        };

        foreach (var metricId in metricIds)
        {
            try
            {
                response.Metrics.Add(await RunMetricAsync(metricId, devices, options, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单指标异常（如压测闸超时、目标不可达）降级为该指标不达标，不拖垮整个任务。
                _logger.LogWarning(ex, "验证指标 {MetricId} 执行异常", metricId);
                response.Metrics.Add(Fail(metricId, "", metricId, "执行异常", ex.Message, []));
            }
        }

        response.CompletedAt = Now();
        response.Status = response.Metrics.All(x => x.Status == "passed") ? "completed" : "failed";
        var passed = response.Metrics.Count(x => x.Status == "passed");
        response.Result = response.Status == "completed" ? "通过" : "不通过";
        response.Detail = $"自动验证完成：达标 {passed}/{response.Metrics.Count} 项";
        return response;
    }

    private async Task<VerifyMetricResult> RunMetricAsync(
        string metricId, IReadOnlyList<DeviceSnapshot> devices, VerifyRunOptions options, CancellationToken ct) =>
        metricId switch
        {
            "industrial-protocol" => CheckIndustrialProtocol(devices),
            "communication-stability" => await CheckCommunicationStabilityAsync(devices, options, ct),
            "max-connections" => await CheckMaxConnectionsAsync(devices, options, ct),
            "transfer-protocol" => CheckTransferProtocol(devices),
            "file-integrity" => await CheckFileIntegrityAsync(devices, ct),
            "transfer-speed" => await CheckTransferSpeedAsync(devices, ct),
            "file-size" => await CheckFileSizeAsync(devices, ct),
            _ => Fail(metricId, "", metricId, "未知指标", "未找到自动验收实现", []),
        };

    /// <summary>按指标库中同 Code 指标的阈值判定达标（实测值 ≥ 阈值）；未配置阈值时用内置默认判据。</summary>
    private (bool Passed, string Reference) Judge(string code, double value, bool fallbackPassed, string fallbackReference)
    {
        MetricDto? metric = null;
        try
        {
            metric = _metricStore.ReadAll().FirstOrDefault(x =>
                x.Code.Equals(code, StringComparison.OrdinalIgnoreCase) && x.Threshold is not null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取指标库失败，退回默认判据");
        }
        if (metric?.Threshold is { } threshold)
        {
            var unit = string.IsNullOrWhiteSpace(metric.Unit) ? "" : $" {metric.Unit}";
            return (value >= threshold, $"指标库标准（{metric.Name}）：实测 ≥ {threshold}{unit}");
        }
        return (fallbackPassed, fallbackReference);
    }

    private VerifyMetricResult CheckIndustrialProtocol(IReadOnlyList<DeviceSnapshot> devices)
    {
        var protocols = devices.Select(x => x.Protocol).Where(NotBlank).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var expected = new[] { "Profibus", "ModbusTCP", "NCLinkApi", "FOCAS", "OpcUa" };
        var covered = expected.Count(x => protocols.Contains(x, StringComparer.OrdinalIgnoreCase));
        var (passed, reference) = Judge("5.2.1", covered, covered > 0, "至少发现 1 种已配置工控协议");
        return Build("industrial-protocol", "5.2.1", "工控协议覆盖率", passed,
            $"{covered}/{expected.Length} 种", reference,
            covered > 0 ? $"已配置协议：{string.Join("、", protocols)}" : "未发现已配置工控协议设备",
            protocols.Select(x => $"protocol={x}"));
    }

    private async Task<VerifyMetricResult> CheckCommunicationStabilityAsync(
        IReadOnlyList<DeviceSnapshot> devices, VerifyRunOptions options, CancellationToken ct)
    {
        var targets = BuildProbeTargets(devices).Take(10).ToList();
        if (targets.Count == 0)
            return Fail("communication-stability", "5.2.2", "通讯稳定性", "连续通讯探测", "无可探测设备或网关", []);

        var success = 0;
        var failure = 0;
        var interruptions = 0; // 中断次数：某目标从探测成功转为失败的次数
        var lastOk = new Dictionary<string, bool>();
        var evidence = new List<string>();
        for (var round = 1; round <= options.CommunicationRounds; round++)
        {
            foreach (var target in targets)
            {
                var result = await _csService.ProbeTcpAsync(target.Host, target.Port, options.ProbeTimeoutMs, ct);
                if (result.Success) success++;
                else
                {
                    failure++;
                    if (lastOk.TryGetValue(target.Label, out var wasOk) && wasOk) interruptions++;
                }
                lastOk[target.Label] = result.Success;
                evidence.Add($"{target.Label} 第{round}轮：{(result.Success ? "OK" : "FAIL")} {result.Message}");
            }
        }

        var total = success + failure;
        var passRate = total == 0 ? 0 : Math.Round(success * 100.0 / total, 1);
        var lossRate = total == 0 ? 0 : Math.Round(failure * 100.0 / total, 1);
        var (passed, reference) = Judge("5.2.2", passRate, failure == 0 && success > 0, "多轮 TCP 探测全部成功（丢包率 0）");
        return Build("communication-stability", "5.2.2", "通讯稳定性", passed,
            $"成功率 {passRate}%", reference,
            $"探测 {targets.Count} 个目标 × {options.CommunicationRounds} 轮：成功 {success}，失败 {failure}，中断次数 {interruptions}，丢包率 {lossRate}%",
            evidence);
    }

    private async Task<VerifyMetricResult> CheckMaxConnectionsAsync(
        IReadOnlyList<DeviceSnapshot> devices, VerifyRunOptions options, CancellationToken ct)
    {
        var first = BuildProbeTargets(devices).FirstOrDefault();
        if (first is null)
            return Fail("max-connections", "5.2.3", "最大并发连接数", "并发 TCP 建连", "无可用于并发压测的目标", []);

        var levels = BuildConcurrencyLevels(options.MaxParallelTargets);
        var bestSuccess = 0;
        var bestLevel = 0;
        CsParallelTestResult? lastResult = null;
        var evidence = new List<string> { $"target={first.Host}:{first.Port}" };

        foreach (var level in levels)
        {
            // 与手动压测共享互斥闸：验证任务愿意等待手动压测结束，而不是抢不到闸就报错。
            var result = await _csService.RunSameTargetParallelTestAsync(
                new CsSameTargetParallelTestRequest(first.Host, first.Port, level, level, options.ProbeTimeoutMs),
                ct, gateWait: TimeSpan.FromMinutes(3));
            lastResult = result;
            if (result.Success > bestSuccess) bestSuccess = result.Success;
            if (result.Success == level) bestLevel = level;
            evidence.Add($"{level}路：成功 {result.Success}/{result.Total}，成功率 {result.SuccessRate}%，平均RTT {result.AvgRttMs}ms");
            evidence.AddRange(result.Failures.Take(3).Select(x => $"{x.DeviceIp}: {x.Error}"));
            if (result.Success < level) break;
        }

        var avgRtt = lastResult?.AvgRttMs ?? 0;
        var (passed, reference) = Judge("5.2.3", bestSuccess,
            bestSuccess >= options.RequiredMinConcurrentSuccess,
            $"自动阶梯压测成功连接数 >= {options.RequiredMinConcurrentSuccess}");
        return Build("max-connections", "5.2.3", "最大并发连接数", passed,
            $"{bestSuccess} 个成功连接", reference,
            $"目标 {first.Label}，最大满额通过 {bestLevel} 路，最高成功 {bestSuccess} 路，最终平均 RTT {avgRtt}ms",
            evidence);
    }

    private VerifyMetricResult CheckTransferProtocol(IReadOnlyList<DeviceSnapshot> devices)
    {
        var protocols = devices.Select(x => x.Transfer?.Protocol).Where(NotBlank).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var (passed, reference) = Judge("5.2.4", protocols.Count, protocols.Count > 0, "至少发现 1 种已配置文件传输协议");
        return Build("transfer-protocol", "5.2.4", "传输协议覆盖率", passed,
            $"{protocols.Count} 种", reference,
            protocols.Count > 0 ? $"已配置传输协议：{string.Join("、", protocols)}" : "未发现配置了文件传输协议的设备",
            protocols.Select(x => $"transferProtocol={x}"));
    }

    private async Task<VerifyMetricResult> CheckFileIntegrityAsync(IReadOnlyList<DeviceSnapshot> devices, CancellationToken ct)
    {
        // 优先按 docx 5.2.5 主动测试：生成文件 → 上传 → 下载 → 对比大小（SHA256 作为增强校验）
        var target = devices.FirstOrDefault(x => x.Transfer is not null && NotBlank(x.Id));
        if (target is not null)
        {
            var active = await TryActiveIntegrityTestAsync(target, ct);
            if (active is not null) return active;
        }

        // 主动测试不可用（无传输设备/上传全部失败）时回退：按真实传输历史留痕判定
        var history = await LoadAllTransferHistoryAsync(devices, ct);
        var completed = history.Where(x => IsCompleted(x.Status)).ToList();
        var consistent = completed.Count(x => x.FileSize > 0 && x.BytesTransferred >= x.FileSize);
        var rate = completed.Count == 0 ? 0 : Math.Round(consistent * 100.0 / completed.Count, 1);
        var (passed, reference) = Judge("5.2.5", rate, consistent > 0, "历史完成传输记录 bytesTransferred >= fileSize");
        return Build("file-integrity", "5.2.5", "文件完整性", passed,
            $"{rate}%（{consistent}/{completed.Count} 条历史记录一致）", reference,
            completed.Count == 0
                ? "无可主动测试的传输设备，且未读取到完成的传输历史"
                : $"主动往返测试不可用，按历史留痕判定：完成 {completed.Count} 条，大小一致 {consistent} 条",
            completed.Take(10).Select(FormatTransferEvidence));
    }

    private async Task<VerifyMetricResult?> TryActiveIntegrityTestAsync(DeviceSnapshot device, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("IndustrialIoT");
        var basePath = _configuration["IndustrialIoT:ProgramTransferPath"] ?? "api/program-transfer";
        // docx 规定 0.1/1/10/20MB 四档；自动验证默认取前两档控制耗时，评价大文件能力见 5.2.7
        var sizes = new[] { 102_400, 1_048_576 };
        var evidence = new List<string> { $"目标设备：{device.Name}（{device.Transfer?.Protocol}）" };
        var uploadedAny = false;
        var passed = 0;
        var tested = 0;

        foreach (var size in sizes)
        {
            ct.ThrowIfCancellationRequested();
            var payload = new byte[size];
            Random.Shared.NextBytes(payload);
            var expectedHash = Convert.ToHexString(SHA256.HashData(payload));
            var fileName = $"verify-integrity-{size}.bin";
            try
            {
                using var form = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(payload);
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                form.Add(fileContent, "file", fileName);
                form.Add(new StringContent("/"), "remotePath");
                using var up = await client.PostAsync($"{basePath}/{Uri.EscapeDataString(device.Id)}/upload", form, ct);
                if (!up.IsSuccessStatusCode)
                {
                    evidence.Add($"{fileName}: 上传失败 HTTP {(int)up.StatusCode}");
                    continue;
                }
                uploadedAny = true;
                tested++;

                var remoteFile = await ExtractRemotePathAsync(up, ct) ?? $"/{fileName}";
                using var down = await client.PostAsync(
                    $"{basePath}/{Uri.EscapeDataString(device.Id)}/download",
                    new StringContent(JsonSerializer.Serialize(new { remotePath = remoteFile }, JsonOptions), Encoding.UTF8, "application/json"),
                    ct);
                if (!down.IsSuccessStatusCode)
                {
                    evidence.Add($"{fileName}: 回读下载失败 HTTP {(int)down.StatusCode}");
                    continue;
                }
                var bytes = await down.Content.ReadAsByteArrayAsync(ct);
                var sizeOk = bytes.Length == size;
                var hashOk = Convert.ToHexString(SHA256.HashData(bytes)) == expectedHash;
                if (sizeOk && hashOk) passed++;
                evidence.Add(
                    $"{fileName}: 大小{(sizeOk ? "一致" : $"不一致（回读 {bytes.Length} / 原始 {size}）")}，SHA256 {(hashOk ? "一致" : "不一致")}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (uploadedAny) tested++;
                evidence.Add($"{fileName}: {ex.Message}");
            }
        }

        if (!uploadedAny)
            return null; // 主动测试无法进行，让调用方回退历史判定

        var rate = tested == 0 ? 0 : Math.Round(passed * 100.0 / tested, 1);
        var (judgePassed, reference) = Judge("5.2.5", rate, tested > 0 && passed == tested, "上传→下载往返，大小与 SHA256 全部一致");
        return Build("file-integrity", "5.2.5", "文件完整性", judgePassed,
            $"{rate}%（{passed}/{tested} 个文件往返一致）", reference,
            $"主动测试：生成随机文件上传至 {device.Name} 后回读比对（大小 + SHA256）",
            evidence);
    }

    /// <summary>从上传响应 JSON 中提取 remotePath（后端返回 ProgramTransferResponse）。</summary>
    private static async Task<string?> ExtractRemotePathAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("remotePath", out var path) && path.ValueKind == JsonValueKind.String)
            {
                var value = path.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private async Task<VerifyMetricResult> CheckTransferSpeedAsync(IReadOnlyList<DeviceSnapshot> devices, CancellationToken ct)
    {
        var history = await LoadAllTransferHistoryAsync(devices, ct);
        var speeds = history.Where(x => IsCompleted(x.Status) && x.DurationMs > 0 && x.BytesTransferred > 0)
            .Select(x => x.BytesTransferred / 1024d / 1024d / (x.DurationMs!.Value / 1000d))
            .ToList();
        var avg = speeds.Count == 0 ? 0 : Math.Round(speeds.Average(), 2);
        var (passed, reference) = Judge("5.2.6", avg, avg > 0, "至少 1 条完成传输记录可计算速度");
        return Build("transfer-speed", "5.2.6", "文件传输速度", passed,
            $"{avg} MB/s", reference,
            speeds.Count == 0 ? "未读取到包含 durationMs 与 bytesTransferred 的完成传输记录" : $"实测留痕：可计算速度记录 {speeds.Count} 条，平均 {avg} MB/s",
            history.Where(x => x.DurationMs > 0).Take(10).Select(FormatTransferEvidence));
    }

    private async Task<VerifyMetricResult> CheckFileSizeAsync(IReadOnlyList<DeviceSnapshot> devices, CancellationToken ct)
    {
        var history = await LoadAllTransferHistoryAsync(devices, ct);
        var max = history.Where(x => IsCompleted(x.Status)).Select(x => Math.Max(x.FileSize, x.BytesTransferred)).DefaultIfEmpty(0).Max();
        var mb = Math.Round(max / 1024d / 1024d, 2);
        var (passed, reference) = Judge("5.2.7", mb, max > 0, "至少 1 条完成传输记录包含文件大小");
        return Build("file-size", "5.2.7", "文件大小", passed,
            $"{mb} MB", reference,
            max > 0 ? $"实测留痕：最大成功传输文件 {mb} MB" : "未读取到可计算文件大小的完成传输记录",
            history.Take(10).Select(FormatTransferEvidence));
    }

    private async Task<IReadOnlyList<DeviceSnapshot>> LoadDevicesAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("IndustrialIoT");
        var path = _configuration["IndustrialIoT:DevicesPath"] ?? "api/Devices";
        try
        {
            using var response = await client.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("自动验收读取设备失败: HTTP {Status}", (int)response.StatusCode);
                return [];
            }
            var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<List<DeviceSnapshot>>(stream, JsonOptions, ct) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "自动验收读取设备异常");
            return [];
        }
    }

    private async Task<IReadOnlyList<TransferHistoryItem>> LoadAllTransferHistoryAsync(
        IReadOnlyList<DeviceSnapshot> devices, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("IndustrialIoT");
        var basePath = _configuration["IndustrialIoT:ProgramTransferPath"] ?? "api/program-transfer";
        var output = new List<TransferHistoryItem>();
        foreach (var device in devices.Where(x => NotBlank(x.Id)))
        {
            try
            {
                using var response = await client.GetAsync($"{basePath}/{Uri.EscapeDataString(device.Id)}/history", ct);
                if (!response.IsSuccessStatusCode) continue;
                var stream = await response.Content.ReadAsStreamAsync(ct);
                var items = await JsonSerializer.DeserializeAsync<List<TransferHistoryItem>>(stream, JsonOptions, ct) ?? [];
                foreach (var item in items) item.DeviceId = string.IsNullOrWhiteSpace(item.DeviceId) ? device.Id : item.DeviceId;
                output.AddRange(items);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取设备 {DeviceId} 传输历史失败", device.Id);
            }
        }
        return output;
    }

    private List<ProbeTarget> BuildProbeTargets(IReadOnlyList<DeviceSnapshot> devices)
    {
        var targets = devices
            .Where(x => NotBlank(x.Host) && x.Port is > 0 and <= 65535)
            .Select(x => new ProbeTarget(x.Name, x.Host, x.Port))
            .ToList();
        foreach (var gw in _csService.ListGateways().Where(x => NotBlank(x.Ip) && x.Port is > 0 and <= 65535))
            targets.Add(new ProbeTarget(gw.Name, gw.Ip, gw.Port));
        return targets;
    }

    private static IReadOnlyList<int> BuildConcurrencyLevels(int maxParallelTargets)
    {
        var levels = new[] { 10, 25, 50, 100, 200, 500 }
            .Where(x => x <= maxParallelTargets)
            .ToList();
        if (levels.Count == 0 || levels[^1] != maxParallelTargets)
            levels.Add(maxParallelTargets);
        return levels.Distinct().OrderBy(x => x).ToList();
    }

    private static VerifyRunOptions NormalizeOptions(VerifyRunOptions? options)
    {
        options ??= new VerifyRunOptions();
        options.CommunicationRounds = Math.Clamp(options.CommunicationRounds <= 0 ? 3 : options.CommunicationRounds, 1, 10);
        options.ProbeTimeoutMs = Math.Clamp(options.ProbeTimeoutMs <= 0 ? 3000 : options.ProbeTimeoutMs, 500, 30000);
        // 上限对齐 CsConnectivityService.MaxConcurrent：压测引擎会把连接数钳制到该值，
        // 若此处允许更大值，RequiredMinConcurrentSuccess 将永远无法达标。
        options.MaxParallelTargets = Math.Clamp(options.MaxParallelTargets <= 0 ? 50 : options.MaxParallelTargets,
            1, CsConnectivityService.MaxConcurrent);
        options.RequiredMinConcurrentSuccess = options.RequiredMinConcurrentSuccess <= 0
            ? options.MaxParallelTargets
            : Math.Clamp(options.RequiredMinConcurrentSuccess, 1, options.MaxParallelTargets);
        return options;
    }

    private static IReadOnlyList<string> NormalizeMetricIds(IReadOnlyList<string>? metricIds)
    {
        var defaults = new[]
        {
            "industrial-protocol", "communication-stability", "max-connections", "transfer-protocol",
            "file-integrity", "transfer-speed", "file-size"
        };
        return metricIds?.Where(NotBlank).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } list
            ? list
            : defaults;
    }

    private static VerifyMetricResult Build(string metricId, string code, string name, bool passed,
        string value, string reference, string detail, IEnumerable<string> evidence) => new()
    {
        MetricId = metricId,
        Code = code,
        Name = name,
        Status = passed ? "passed" : "failed",
        Result = passed ? "达标" : "未达标",
        Value = value,
        Reference = reference,
        Detail = detail,
        Evidence = evidence.Where(NotBlank).Take(20).ToList(),
    };

    private static VerifyMetricResult Fail(string metricId, string code, string name,
        string reference, string detail, IEnumerable<string> evidence) =>
        Build(metricId, code, name, false, "-", reference, detail, evidence);

    private static bool IsCompleted(string? status) =>
        string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "完成", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase);

    private static bool NotBlank(string? value) => !string.IsNullOrWhiteSpace(value);
    private static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static string FormatTransferEvidence(TransferHistoryItem item) =>
        $"{item.DeviceId}/{item.FileName}: {item.Status}, fileSize={item.FileSize}, bytes={item.BytesTransferred}, durationMs={item.DurationMs}";

    private sealed record ProbeTarget(string Label, string Host, int Port);

    private sealed class DeviceSnapshot
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Protocol { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public TransferSnapshot? Transfer { get; set; }
    }

    private sealed class TransferSnapshot
    {
        public string Protocol { get; set; } = "";
    }

    private sealed class TransferHistoryItem
    {
        public string DeviceId { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Status { get; set; } = "";
        public long FileSize { get; set; }
        public long BytesTransferred { get; set; }
        public double? DurationMs { get; set; }
    }
}
