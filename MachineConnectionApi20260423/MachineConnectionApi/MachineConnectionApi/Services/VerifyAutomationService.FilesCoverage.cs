using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

public sealed partial class VerifyAutomationService
{
    private async Task MeasureTransferCoverageAsync(VerifyMetricResult metric, EvaluationItem item, EvaluationAutomationRule rule,
        IReadOnlyList<VerifyMeasurementTarget> selected, IReadOnlyList<VerifyMeasurementTarget> all,
        VerifyRunOptions options, Action<double> progress, CancellationToken ct)
    {
        if (!options.AllowFileWrites)
        { MarkUnrated(metric, "未授权上传测试文件，目录读取不能证明上传下载能力。"); return; }
        VerifyTaskOptionsValidation.ValidateTransferTargets(options.TransferDeviceIds);
        var targets = new List<VerifyMeasurementTarget>();
        foreach (var id in options.TransferDeviceIds is { Count: > 0 } explicitIds
            ? explicitIds : selected.Select(target => target.Id))
        {
            var matches = all.Where(target => target.Id == id).ToArray();
            if (matches.Length != 1)
            { MarkUnrated(metric, $"传输目标 {id} 不存在或标识不唯一，本轮未上传。"); return; }
            targets.Add(matches[0]);
        }
        var standards = item.Protocols.Select(EvaluationAutomationRules.NormalizeProtocol)
            .Where(protocol => protocol.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (standards.Length == 0)
        { MarkUnrated(metric, "未选择传输协议标准集合，本轮未上传。"); return; }
        string Protocol(VerifyMeasurementTarget target) => EvaluationAutomationRules.NormalizeProtocol(
            target.ResolvedTransferProtocol ?? target.Transfer?.Protocol ?? target.Protocol);
        // The Host device DTO does not always carry the inline transfer block (older rows / restart).
        // Resolve the protocol from the Host verification snapshot so a correctly configured FTP/SMB/NFS
        // device is not reported as missing an explicit transfer target.
        foreach (var target in targets)
        {
            if (target.ResolvedTransferProtocol is not null || target.Transfer is not null) continue;
            var snapshot = await _measurements.GetTransferSnapshotAsync(target, ct);
            if (snapshot.Supported && !string.IsNullOrWhiteSpace(snapshot.Protocol))
                target.ResolvedTransferProtocol = snapshot.Protocol;
            metric.Evidence.Add($"{target.Id}: transfer snapshot {snapshot.Detail}");
        }

        if (standards.Any(protocol => !targets.Any(target => Protocol(target) == protocol)))
        { MarkUnrated(metric, "部分标准协议缺少显式传输目标，不能把未测协议当作支持或不支持；本轮未上传。"); return; }
        var samples = item.Files.Where(file => file.SizeBytes is { } bytes
            && Math.Abs(bytes - 0.1 * 1024 * 1024) <= 1).ToArray();
        if (samples.Length != 1)
        { MarkUnrated(metric, "需要一个实际大小为0.1 MiB的已上传校验附件，本轮未上传。"); return; }
        var sample = samples[0];
        var validation = await _measurements.ValidateAttachmentAsync(sample, ct);
        metric.Evidence.Add($"校验附件 {sample.Id}/{sample.Name}: {validation.Detail}");
        if (!validation.Valid)
        { MarkUnrated(metric, "校验附件无效或不存在，本轮未上传。"); return; }
        var verified = new List<string>();
        var unknown = false;
        // Each standard protocol is counted once; a verified target establishes that protocol's ability.
        for (var protocolIndex = 0; protocolIndex < standards.Length; protocolIndex++)
        {
            var protocol = standards[protocolIndex];
            var candidates = targets.Where(target => Protocol(target) == protocol).ToArray();
            var supportedEvidence = false;
            var hasUnknown = false;
            var succeeded = false;
            for (var targetIndex = 0; targetIndex < candidates.Length; targetIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var stage = protocolIndex;
                var candidateIndex = targetIndex;
                var remoteName = $"verify-{Guid.NewGuid():N}{Path.GetExtension(sample.Name).ToLowerInvariant()}";
                var outcome = await _measurements.TransferAsync(candidates[targetIndex], sample,
                    rule.Test.TargetDirectory, remoteName,
                    fraction => progress((stage + (candidateIndex + Math.Clamp(fraction, 0, 1)) / candidates.Length)
                        * 100 / standards.Length), ct);
                metric.Evidence.Add($"{candidates[targetIndex].Id}/{protocol}，本轮文件 {remoteName}: {outcome.Detail}");
                if (!outcome.Supported || !outcome.HasEvidence)
                { hasUnknown = true; continue; }
                if (outcome.Success && (!outcome.IntegrityVerified || outcome.UploadedBytes != sample.SizeBytes))
                { hasUnknown = true; continue; }
                supportedEvidence = true;
                if (outcome.Success && outcome.IntegrityVerified)
                { succeeded = true; break; }
            }
            if (succeeded) verified.Add(protocol);
            else if (hasUnknown || !supportedEvidence) unknown = true;
            progress((protocolIndex + 1d) * 100 / standards.Length);
        }
        if (unknown)
        { MarkUnrated(metric, "部分传输协议缺少可靠的本轮上传及回读校验证据，不能评分为0或计入覆盖种数。"); return; }
        SetMeasurement(metric, rule, verified.Count, "count",
            "按标准协议中本轮完整附件上传及下载回读SHA256一致的协议种数计分；同协议只计一次，目录不计分。");
        metric.Detail = $"上传及回读校验通过 {verified.Count}/{standards.Length} 种：{string.Join("、", verified)}";
    }
}
