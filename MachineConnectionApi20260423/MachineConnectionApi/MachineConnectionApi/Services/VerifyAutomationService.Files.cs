using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

public sealed partial class VerifyAutomationService
{
    private async Task MeasureFilesAsync(VerifyMetricResult metric, EvaluationItem item, EvaluationAutomationRule rule,
        IReadOnlyList<VerifyMeasurementTarget> devices, VerifyRunOptions options, Action<double> progress, CancellationToken ct)
    {
        if (!options.AllowFileWrites)
        {
            MarkUnrated(metric, "未授权向设备写入测试文件；没有发起上传，不使用历史传输代替本轮测试。");
            return;
        }
        if (devices.Count != 1)
        { MarkUnrated(metric, "文件测试必须明确选择一个设备，不自动选择或写入其他设备。"); return; }
        var sizes = metric.MetricId switch
        {
            "file-integrity" => new[] { 0.1, 1, 10, 20 },
            "transfer-speed" => new[] { 10d },
            "file-size" => new[] { 1d, 10, 200 },
            _ => throw new ArgumentException("没有定义的文件测试。"),
        };
        var files = new List<EvaluationFile>();
        var missing = false;
        foreach (var size in sizes)
        {
            // 0.1 MiB cannot be an integral number of bytes: accept the adjacent rounded byte only.
            var candidates = item.Files.Where(x => x.SizeBytes is { } bytes && Math.Abs(bytes - size * 1024 * 1024) <= 1).ToList();
            if (candidates.Count != 1)
            {
                metric.Evidence.Add($"{size:0.###} MiB：需要且只允许一个匹配实际大小的已上传附件；找到 {candidates.Count} 个");
                missing = true;
                continue;
            }
            var file = candidates[0];
            var validation = await _measurements.ValidateAttachmentAsync(file, ct);
            metric.Evidence.Add($"{size:0.###} MiB 附件 {file.Name}/{file.Id}: {validation.Detail}");
            if (!validation.Valid) missing = true;
            else files.Add(file);
        }
        if (missing)
        {
            MarkUnrated(metric, "配置测试附件缺失、大小不符或仅有默认文件名；本轮未写入设备，不生成替代文件，也不读取历史记录评分。");
            return;
        }
        var passed = 0;
        var largest = 0d;
        var speed = 0d;
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            var index = i;
            var extension = Path.GetExtension(file.Name).ToLowerInvariant();
            var remoteName = $"verify-{Guid.NewGuid():N}{extension}";
            var outcome = await _measurements.TransferAsync(devices[0], file, rule.Test.TargetDirectory, remoteName,
                fraction => progress((index + Math.Clamp(fraction, 0, 1)) * 100 / files.Count), ct);
            metric.Evidence.Add($"附件 {file.Id}/{file.Name}，本轮远端文件 {remoteName}: {outcome.Detail}");
            if (!outcome.Supported || !outcome.HasEvidence)
            {
                MarkUnrated(metric, "当前设备/协议未提供安全文件往返测量，或没有可靠本轮传输证据；不能评为0分。" + outcome.Detail);
                return;
            }
            if (outcome.Success && outcome.IntegrityVerified)
            {
                if (outcome.UploadedBytes != file.SizeBytes!.Value)
                { MarkUnrated(metric, "本轮成功标记与实际上传字节数不一致，不能证明完整附件已传输。"); return; }
                passed++;
                largest = file.SizeBytes!.Value / 1024d / 1024d;
                if (metric.MetricId == "transfer-speed")
                {
                    if (!double.IsFinite(outcome.UploadSeconds) || outcome.UploadSeconds <= 0)
                    { MarkUnrated(metric, "本轮10MiB上传缺少有效字节数或计时，不能计算速度。"); return; }
                    speed = outcome.UploadedBytes / 1024d / 1024d / outcome.UploadSeconds;
                }
            }
            progress((i + 1d) / files.Count * 100);
            if (metric.MetricId == "file-size" && (!outcome.Success || !outcome.IntegrityVerified)) break;
        }
        var value = metric.MetricId switch
        {
            "file-integrity" => passed * 100d / files.Count,
            "transfer-speed" => speed,
            _ => largest,
        };
        var unit = metric.MetricId switch { "file-integrity" => "%", "transfer-speed" => "MB/s", _ => "MB" };
        SetMeasurement(metric, rule, value, unit, metric.MetricId switch
        {
            "file-integrity" => "本轮0.1/1/10/20MiB四档真实附件上传及回读的大小、SHA256一致率。",
            "transfer-speed" => "本轮10MiB附件设备上传计时（不含HTTP接收、本地哈希和回读），回读校验通过后计算MiB/s；不是历史均速。",
            _ => "本轮1/10/200MiB逐级上传并回读校验，记录最大成功附件大小；不代表超出测试上限的极限。",
        });
        metric.Detail = metric.ScoreReason;
        metric.Evidence.Add("使用验证专用原子独占创建契约；不会执行程序或覆盖已有对象，只清理本轮独占对象；各次清理结果见对应传输证据。");
    }
}
