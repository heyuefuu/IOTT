using MachineConnectionApi.Models;
using static MachineConnectionApi.Services.EvaluationAutomationRules;

namespace MachineConnectionApi.Services;

public sealed partial class VerifyAutomationService
{
    private async Task MeasureProtocolsAsync(VerifyMetricResult metric, EvaluationItem item, EvaluationAutomationRule rule,
        IReadOnlyList<VerifyMeasurementTarget> devices, VerifyRunOptions options, bool transfer, Action<double> progress, CancellationToken ct)
    {
        var standards = item.Protocols.Select(NormalizeProtocol).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var configured = devices.Select(x => NormalizeProtocol(transfer ? x.ResolvedTransferProtocol ?? x.Transfer?.Protocol ?? x.Protocol : x.Protocol))
            .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        metric.MeasurementSource = "configuration";
        metric.Evidence.Add($"配置协议（不等于能力实测）：{string.Join("、", configured)}");
        metric.Evidence.Add($"待评价标准集合（别名去重）：{string.Join("、", standards)}");
        if (standards.Count == 0) { MarkUnrated(metric, "未选择协议标准集合，不能计算协议覆盖评分。"); return; }
        var candidates = devices.Where(x => standards.Contains(NormalizeProtocol(transfer ? x.ResolvedTransferProtocol ?? x.Transfer?.Protocol ?? x.Protocol : x.Protocol))).ToList();
        if (candidates.Count == 0) { MarkUnrated(metric, "只有协议配置统计，没有选定标准协议的本轮通讯证据。"); return; }
        var verified = new HashSet<string>(StringComparer.Ordinal);
        var measured = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++)
        {
            var device = candidates[i];
            var protocol = NormalizeProtocol(transfer ? device.ResolvedTransferProtocol ?? device.Transfer?.Protocol ?? device.Protocol : device.Protocol);
            var observation = transfer
                ? await _measurements.ReadTransferDirectoryAsync(device, rule.Test.TargetDirectory, options.ProbeTimeoutMs, ct)
                : await _measurements.ReadAsync(device, rule.Test.ReadAddress, rule.Test.ReadDataType, options.ProbeTimeoutMs, ct);
            metric.Evidence.Add($"{device.Name}/{protocol}: {observation.Detail}");
            if (!string.IsNullOrWhiteSpace(observation.Protocol) && NormalizeProtocol(observation.Protocol) != protocol)
            {
                MarkUnrated(metric, $"本轮实际协议 {observation.Protocol} 与设备快照配置 {protocol} 不一致；不能归属到该配置协议评分。");
                return;
            }
            if (observation.Supported && observation.HasEvidence)
            {
                measured.Add(protocol);
                if (observation.Success) verified.Add(protocol);
            }
            progress((i + 1d) * 100 / candidates.Count);
        }
        if (measured.Count == 0) { MarkUnrated(metric, "未取得本轮真实协议通讯证据，配置数量不计作能力验证分数。"); return; }
        // An unsupported/missing read for a configured protocol makes its ability unknown, rather than zero.
        if (candidates.Select(x => NormalizeProtocol(transfer ? x.ResolvedTransferProtocol ?? x.Transfer?.Protocol ?? x.Protocol : x.Protocol)).Distinct().Any(x => !measured.Contains(x)))
        { MarkUnrated(metric, "部分已配置协议缺少本轮可验证通讯证据，不能把未测协议计为不支持。"); return; }
        var value = transfer ? verified.Count : Math.Min(100, verified.Count * 100d / standards.Count);
        SetMeasurement(metric, rule, value, transfer ? "count" : "%", transfer
            ? "按选定集合中成功完成本轮目录协议读取的协议种类计分；不是上传能力证明。"
            : "按选定标准集合中本轮应用读成功的协议覆盖比例计分。");
        metric.Detail = $"实测支持 {verified.Count}/{standards.Count} 种：{string.Join("、", verified)}；配置与实测已区分";
    }

    private async Task MeasureStabilityAsync(VerifyMetricResult metric, EvaluationAutomationRule rule,
        IReadOnlyList<VerifyMeasurementTarget> devices, VerifyRunOptions options, Action<double> progress, CancellationToken ct)
    {
        if (devices.Count == 0) { MarkUnrated(metric, "没有选定设备，不能执行持续通讯。"); return; }
        var duration = TimeSpan.FromMinutes(rule.Test.DurationMinutes);
        var interval = TimeSpan.FromSeconds(rule.Test.SampleIntervalSeconds);
        if (duration <= TimeSpan.Zero || interval <= TimeSpan.Zero) throw new ArgumentException("持续通讯时长和采样间隔必须大于零。");
        TimeSpan? observationStart = null;
        TimeSpan? segmentStart = null;
        var longest = TimeSpan.Zero;
        var success = 0;
        var failure = 0;
        var interruptions = 0;
        var previouslySucceeded = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var observations = await Task.WhenAll(devices.Select(device =>
                _measurements.ReadAsync(device, rule.Test.ReadAddress, rule.Test.ReadDataType, options.ProbeTimeoutMs, ct)));
            // Observe a full configured window after the first actual sample. Counting from before
            // the initial request would make an all-successful 30 min run always measure <30 min.
            observationStart ??= _clock.Elapsed;
            var elapsed = _clock.Elapsed - observationStart.Value;
            var sampleTime = elapsed > duration ? duration : elapsed;
            foreach (var (device, observation) in devices.Zip(observations))
                AddSampleEvidence(metric, $"{elapsed.TotalSeconds:0.###}s {device.Name}: {observation.Detail}");
            if (observations.Any(x => !x.Supported || !x.HasEvidence))
            {
                MarkUnrated(metric, "持续通讯缺少真实应用/协议读支持或可靠测量，TCP握手不替代持续通讯。");
                return;
            }
            var allOk = observations.All(x => x.Success);
            success += observations.Count(x => x.Success);
            failure += observations.Count(x => !x.Success);
            if (allOk)
            {
                segmentStart ??= sampleTime;
                var current = sampleTime - segmentStart.Value;
                if (current > longest) longest = current;
            }
            else
            {
                if (previouslySucceeded) interruptions++;
                segmentStart = null;
            }
            previouslySucceeded = allOk;
            metric.MeasurementSource = "current-run";
            metric.Detail = $"应用读成功 {success}，失败 {failure}，中断 {interruptions}；观察 {elapsed.TotalMinutes:0.####} min，最长连续 {longest.TotalMinutes:0.####} min";
            progress(Math.Min(99.9, elapsed.TotalMilliseconds / duration.TotalMilliseconds * 100));
            if (elapsed >= duration) break;
            await _clock.DelayAsync(TimeSpan.FromMilliseconds(Math.Min(interval.TotalMilliseconds, (duration - elapsed).TotalMilliseconds)), ct);
        }
        metric.Evidence.Add(metric.Detail);
        SetMeasurement(metric, rule, longest.TotalMinutes, "min", "按本轮采样中所有选定设备同时应用读成功的最长连续时段计分；不是TCP建连或历史在线时长。");
    }

    private async Task MeasureConcurrencyAsync(VerifyMetricResult metric, EvaluationAutomationRule rule,
        IReadOnlyList<VerifyMeasurementTarget> selected, IReadOnlyList<VerifyMeasurementTarget> all, VerifyRunOptions options,
        Action<double> progress, CancellationToken ct)
    {
        if (!string.Equals(rule.Test.ConcurrencyMode, "devices", StringComparison.Ordinal))
        {
            MarkUnrated(metric, $"{rule.Test.ConcurrencyMode}模式需要独立同设备连接测量适配器；现有HTTP读复用连接，不能冒充独立连接数或设备数。");
            return;
        }
        var ids = options.ConcurrentDeviceIds?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (ids.Count == 0)
        {
            MarkUnrated(metric, "请显式选择参与并发测试的设备；不会将单机任务自动扩展到全部设备。");
            return;
        }
        var targets = new List<VerifyMeasurementTarget>();
        foreach (var id in ids)
        {
            var matches = all.Where(x => x.Id == id).ToList();
            if (matches.Count != 1) { MarkUnrated(metric, $"参与并发测试的设备 {id} 不存在或标识不唯一。"); return; }
            targets.Add(matches[0]);
        }
        // Separate device ids pointing to the same endpoint do not demonstrate more physical targets.
        if (targets.Where(x => !string.IsNullOrWhiteSpace(x.Host))
            .GroupBy(x => $"{x.Host.Trim().ToLowerInvariant()}:{x.Port}").Any(x => x.Count() > 1))
        { MarkUnrated(metric, "参与设备包含重复网络目标，不能作为不同设备并发数。"); return; }
        var maximum = rule.Test.MaxConnections;
        var limit = Math.Min(targets.Count, maximum);
        if (maximum < 1 || rule.Test.FailureLimit < 1) throw new ArgumentException("并发上限及连续失败次数必须大于零。");
        var best = 0;
        var stoppedByFailures = false;
        metric.Evidence.Add($"devices模式，显式目标 {string.Join(",", ids)}；每档+1，上限 {maximum}，连续失败 {rule.Test.FailureLimit} 次终止");
        for (var level = 1; level <= limit; level++)
        {
            var fullSuccess = false;
            for (var attempt = 1; attempt <= rule.Test.FailureLimit; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                // HTTP application reads are dispatched together against distinct selected devices.
                var observations = await Task.WhenAll(targets.Take(level).Select(device =>
                    _measurements.ReadAsync(device, rule.Test.ReadAddress, rule.Test.ReadDataType, options.ProbeTimeoutMs, ct)));
                metric.Evidence.Add($"{level}设备并发，第{attempt}次：成功 {observations.Count(x => x.Success)}/{level}；{string.Join("；", observations.Select(x => x.Detail))}");
                if (observations.Any(x => !x.Supported || !x.HasEvidence))
                { MarkUnrated(metric, "并发档位缺少真实应用读证据，未执行档位不能计作成功连接。"); return; }
                progress(((level - 1d) + attempt / (double)rule.Test.FailureLimit) / maximum * 100);
                if (observations.All(x => x.Success)) { best = level; fullSuccess = true; break; }
            }
            if (!fullSuccess) { stoppedByFailures = true; break; }
        }
        SetMeasurement(metric, rule, best, "count", "本轮显式不同设备的并发应用读最大完整成功档位；与最多7个任务调度并行无关。");
        metric.Detail = $"最大完整成功 {best} 个设备；配置上限 {maximum}，可用显式设备 {targets.Count}";
        if (!stoppedByFailures && targets.Count < maximum)
        {
            MarkUnrated(metric, $"仅验证到 {best} 个设备的下限；所选设备 {targets.Count} 个不足配置上限 {maximum}，不能据此声称测得最大值。");
        }
    }

    private static void AddSampleEvidence(VerifyMetricResult metric, string text)
    {
        // Bound snapshot size for a long run while keeping the earliest sample and recent transitions.
        if (metric.Evidence.Count >= 256) metric.Evidence.RemoveAt(1);
        metric.Evidence.Add(text);
    }
}
