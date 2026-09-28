using System.Globalization;
using System.Text.Json;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

public interface IVerifyAutomationService
{
    Task<VerifyRunResponse> RunAsync(VerifyRunRequest request, CancellationToken ct);
}

/// <summary>
/// Evaluation automation consumes the versioned item rules, never the legacy metrics.json thresholds.
/// Execution, acceptance and scoring are independent; missing measurement evidence is not a zero score.
/// </summary>
public sealed partial class VerifyAutomationService : IVerifyAutomationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] MetricIds =
    [
        "industrial-protocol", "communication-stability", "max-connections", "transfer-protocol",
        "file-integrity", "transfer-speed", "file-size"
    ];
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<VerifyAutomationService> _logger;
    private readonly EvaluationIndicatorStore? _evaluationIndicators;
    private readonly IVerifyMeasurementAdapter _measurements;
    private readonly IVerifyTestClock _clock;

    // Keep the previous constructor's parameters for DI and older callers. The legacy metric and TCP
    // services are intentionally not consulted: they cannot supply the required evaluation evidence.
    public VerifyAutomationService(
        IHttpClientFactory httpClientFactory,
        ICsConnectivityService csService,
        IMetricStore metricStore,
        IConfiguration configuration,
        ILogger<VerifyAutomationService> logger,
        EvaluationIndicatorStore? evaluationIndicators = null,
        IVerifyMeasurementAdapter? measurements = null,
        IVerifyTestClock? clock = null)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _evaluationIndicators = evaluationIndicators;
        _measurements = measurements ?? new HttpVerifyMeasurementAdapter(httpClientFactory);
        _clock = clock ?? new SystemVerifyTestClock();
    }

    public async Task<VerifyRunResponse> RunAsync(VerifyRunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.DeviceId))
            throw new ArgumentException("请明确选择本次验证的设备；不允许默认测试全部设备。", nameof(request));
        var ids = request.MetricIds?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal).ToList() ?? MetricIds.ToList();
        if (ids.Count == 0) ids = MetricIds.ToList();
        if (ids.Any(x => !MetricIds.Contains(x, StringComparer.Ordinal)))
            throw new ArgumentException("所选指标没有已定义的自动测试实现。");

        // Clone even when a caller supplies a mutable store implementation. This same snapshot drives
        // the whole run and is the source of the eventual knowledge evaluation.
        var evaluation = _evaluationIndicators is null ? null : Clone(_evaluationIndicators.Get(request.EvaluationCategory));
        var items = evaluation?.Indicators.SelectMany(x => x.Children).SelectMany(x => x.Items)
            .Where(x => x.MetricId is not null).ToDictionary(x => x.MetricId!, StringComparer.Ordinal);
        if (items is not null && ids.Any(x => !items.ContainsKey(x)))
            throw new ArgumentException("所选指标已从评价指标管理中移除，请重新选择任务指标。");
        var options = request.Options is null ? new VerifyRunOptions() : Clone(request.Options);
        options.ProbeTimeoutMs = Math.Clamp(options.ProbeTimeoutMs <= 0 ? 3000 : options.ProbeTimeoutMs, 500, 30000);
        var response = new VerifyRunResponse
        {
            RunId = Guid.NewGuid().ToString("N"), TaskId = request.TaskId,
            TaskName = string.IsNullOrWhiteSpace(request.TaskName) ? "自动验证任务" : request.TaskName.Trim(),
            StartedAt = Now(), Status = "running", DeviceId = request.DeviceId?.Trim(),
            EvaluationSnapshot = evaluation, OptionsSnapshot = Clone(options), TotalMetricCount = ids.Count,
            Metrics = ids.Select(id => new VerifyMetricResult
            {
                MetricId = id, Code = $"5.2.{Array.IndexOf(MetricIds, id) + 1}",
                Name = items?.GetValueOrDefault(id)?.Name ?? id,
                ExecutionStatus = "pending", Status = "unrated", ScoreReason = "等待本轮实测",
            }).ToList(),
        };
        void Publish()
        {
            response.CompletedMetricCount = response.Metrics.Count(x => x.ExecutionStatus is "completed" or "skipped" or "error");
            response.ProgressPercent = Math.Round(response.Metrics.Sum(x => x.ProgressPercent) / ids.Count, 2);
            // A synchronous snapshot callback: neither the runner nor a UI callback retains references
            // that subsequent engine updates can mutate behind its back.
            request.ReportProgress?.Invoke(Clone(response));
        }
        Publish();
        IReadOnlyList<VerifyMeasurementTarget> devices;
        try
        {
            devices = await LoadDevicesAsync(ct);
            if (!string.IsNullOrWhiteSpace(response.DeviceId))
            {
                var selected = devices.Where(x => x.Id == response.DeviceId).ToList();
                if (selected.Count != 1) throw new InvalidOperationException("所选设备不存在或标识不唯一，请检查设备配置与上游同步状态。");
                response.MachineSnapshot = SnapshotMachine(selected[0]);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            foreach (var metric in response.Metrics) MarkError(metric, "读取本轮设备失败：" + ex.Message);
            Complete(response);
            Publish();
            return response;
        }
        var selectedDevices = devices.Where(x => x.Id == response.DeviceId).ToList();

        foreach (var metric in response.Metrics)
        {
            ct.ThrowIfCancellationRequested();
            response.CurrentMetricId = metric.MetricId;
            metric.ExecutionStatus = "running";
            Publish();
            try
            {
                var item = items?.GetValueOrDefault(metric.MetricId);
                var rule = item?.Automation;
                if (item is null || rule is null)
                    MarkUnrated(metric, "未配置评价自动测试规则；旧指标库阈值不会用于本次评价。");
                else
                {
                    EvaluationAutomationRules.Validate(item);
                    metric.Unit = rule.Unit;
                    await MeasureAsync(metric, item, rule, selectedDevices, devices, options,
                        progress => { metric.ProgressPercent = Math.Clamp(progress, 0, 99.9); Publish(); }, ct);
                    ApplyRule(metric, rule);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "评价指标 {MetricId} 执行异常", metric.MetricId);
                MarkError(metric, ex.Message);
            }
            if (metric.ExecutionStatus == "running") metric.ExecutionStatus = "completed";
            metric.ProgressPercent = 100;
            Publish();
        }
        Complete(response);
        Publish();
        return response;
    }

    private Task MeasureAsync(VerifyMetricResult metric, EvaluationItem item, EvaluationAutomationRule rule,
        IReadOnlyList<VerifyMeasurementTarget> selected, IReadOnlyList<VerifyMeasurementTarget> all,
        VerifyRunOptions options, Action<double> progress, CancellationToken ct) => metric.MetricId switch
        {
            "industrial-protocol" => MeasureProtocolsAsync(metric, item, rule, selected, options, false, progress, ct),
            "transfer-protocol" => MeasureProtocolsAsync(metric, item, rule, selected, options, true, progress, ct),
            "communication-stability" => MeasureStabilityAsync(metric, rule, selected, options, progress, ct),
            "max-connections" => MeasureConcurrencyAsync(metric, rule, selected, all, options, progress, ct),
            _ => MeasureFilesAsync(metric, item, rule, selected, options, progress, ct),
        };

    private static void ApplyRule(VerifyMetricResult metric, EvaluationAutomationRule rule)
    {
        metric.Reference = rule.PassRule is null ? "达标线未配置" :
            $"{rule.PassRule.Comparison} {rule.PassRule.Threshold.ToString(CultureInfo.InvariantCulture)} {rule.PassRule.Unit}";
        if (metric.ExecutionStatus is "error" or "skipped" || metric.Measurement is not { } value)
        {
            metric.Score = null;
            if (metric.Status != "error") { metric.Status = "unrated"; metric.Result = "未评分"; }
            return;
        }
        metric.Score = EvaluationAutomationRules.Score(rule, value);
        metric.ScoreReason = metric.Score.HasValue ? "按本次评价快照评分；" + metric.ScoreReason : "评分档位不能计算本轮测量值；" + metric.ScoreReason;
        var accepted = EvaluationAutomationRules.Judge(rule, value);
        metric.Status = accepted switch { true => "passed", false => "failed", _ => "unrated" };
        metric.Result = accepted switch { true => "达标", false => "未达标", _ => "达标线未配置" };
    }

    private static void SetMeasurement(VerifyMetricResult metric, EvaluationAutomationRule rule, double value, string sourceUnit, string reason)
    {
        if (!double.IsFinite(value) || !EvaluationAutomationRules.TryConvert(value, sourceUnit, rule.Unit, out var converted))
        { MarkUnrated(metric, $"测量单位 {sourceUnit} 无法换算至 {rule.Unit}"); return; }
        metric.Measurement = converted;
        metric.Unit = rule.Unit;
        metric.Value = $"{converted.ToString("0.####", CultureInfo.InvariantCulture)} {rule.Unit}";
        metric.MeasurementSource = "current-run";
        metric.ScoreReason = reason;
    }

    private static void MarkUnrated(VerifyMetricResult metric, string reason)
    {
        metric.Status = "unrated"; metric.Result = "未评分"; metric.Score = null;
        metric.ScoreReason = reason; metric.Detail = reason; metric.ExecutionStatus = "skipped";
    }

    private static void MarkError(VerifyMetricResult metric, string reason)
    {
        metric.Status = "error"; metric.Result = "执行错误"; metric.Score = null; metric.Measurement = null;
        metric.ScoreReason = reason; metric.Detail = reason; metric.ExecutionStatus = "error"; metric.ProgressPercent = 100;
    }

    private static void Complete(VerifyRunResponse response)
    {
        response.CompletedAt = Now(); response.CurrentMetricId = null;
        response.Status = response.Metrics.Any(x => x.ExecutionStatus == "error") ? "failed" : "completed";
        var passed = response.Metrics.Count(x => x.Status == "passed");
        response.Result = response.Status == "failed" ? "执行错误" : response.Metrics.Any(x => x.Status == "failed")
            ? "不通过" : response.Metrics.All(x => x.Status == "passed") ? "通过" : "待判定";
        response.TotalScore = response.Metrics.Count > 0 && response.Metrics.All(x => x.Score.HasValue)
            ? Math.Round(response.Metrics.Average(x => x.Score!.Value), 2) : null;
        response.ScoreSummary = response.TotalScore.HasValue ? "本次所选项目单项评分的简单平均（非知识库综合分）"
            : "本次所选项目存在未评分项，任务总分暂不计算（非知识库综合分）";
        response.Detail = $"执行结束：达标 {passed}/{response.Metrics.Count} 项，已评分 {response.Metrics.Count(x => x.Score.HasValue)}/{response.Metrics.Count} 项";
    }

    private async Task<IReadOnlyList<VerifyMeasurementTarget>> LoadDevicesAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("IndustrialIoT");
        using var response = await client.GetAsync(_configuration["IndustrialIoT:DevicesPath"] ?? "api/Devices", ct);
        response.EnsureSuccessStatusCode();
        var devices = await response.Content.ReadFromJsonAsync<List<VerifyMeasurementTarget>>(JsonOptions, ct) ?? [];
        foreach (var device in devices)
        {
            var linkedId = device.ExtendedProperties.GetValueOrDefault("transferDeviceId");
            device.ResolvedTransferProtocol = device.Transfer?.Protocol ?? (string.IsNullOrWhiteSpace(linkedId) || linkedId == device.Id
                ? device.Protocol : devices.SingleOrDefault(x => x.Id == linkedId)?.Protocol ?? "");
        }
        return devices;
    }

    private static EvaluationMachineSnapshot SnapshotMachine(VerifyMeasurementTarget device)
    {
        string Property(params string[] names) => names.Select(name => device.ExtendedProperties.GetValueOrDefault(name))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
        var code = Property("DeviceCode", "deviceCode", "Code", "code");
        var system = Property("ControlSystem", "controlSystem", "System", "system");
        return new EvaluationMachineSnapshot
        {
            Id = device.Id, Name = device.Name, DeviceCode = code.Length == 0 ? device.Id : code,
            Model = device.Model, ControlSystem = system.Length == 0 ? device.Brand : system,
            Host = device.Host, Port = device.Port, Protocol = device.Protocol,
            ConnectTimeoutMs = device.ConnectTimeoutMs, ReadTimeoutMs = device.ReadTimeoutMs,
        };
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
    private static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
}
