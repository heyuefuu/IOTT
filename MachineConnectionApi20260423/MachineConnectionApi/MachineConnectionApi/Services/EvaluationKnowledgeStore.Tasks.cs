using System.Text.Json;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

public sealed partial class EvaluationKnowledgeStore
{
    private static VerifyRunResponse? ParseRun(VerifyTaskDto task)
    {
        if (string.IsNullOrWhiteSpace(task.LastRunJson)) return null;
        try
        {
            var run = JsonSerializer.Deserialize<VerifyRunResponse>(task.LastRunJson, JsonOptions);
            // The editable task/device and CurrentRunJson are not historical evidence.
            return run is not null && !string.IsNullOrEmpty(run.TaskId) && run.TaskId != task.Id ? null : run;
        }
        catch (JsonException) { return null; }
    }

    private static bool CanImport(VerifyRunResponse? run) =>
        run is { Metrics.Count: > 0, MachineSnapshot: not null, EvaluationSnapshot: not null }
        && !string.IsNullOrWhiteSpace(run.RunId)
        && run.Status is "completed" or "failed" or "error" or "cancelled" or "canceled"
        && DateTimeOffset.TryParse(run.CompletedAt, out _)
        && run.Metrics.All(metric => metric is not null)
        && HasValidSnapshot(run.EvaluationSnapshot);

    private static bool HasValidSnapshot(EvaluationConfig snapshot)
    {
        try { EvaluationIndicatorStore.Validate(snapshot); return true; }
        catch (ArgumentException) { return false; }
    }

    public List<KnowledgeTask> ListTasks() => _tasks.ReadAll()
        .Select(task => (Task: task, Run: ParseRun(task)))
        .Where(pair => CanImport(pair.Run))
        .Select(pair =>
        {
            var run = pair.Run!;
            var machine = run.MachineSnapshot!;
            return new KnowledgeTask(pair.Task.Id, pair.Task.Name, machine.Name, machine.DeviceCode,
                machine.Model, machine.ControlSystem, RunDate(run), run.Status, run.Result, run.EvaluationSnapshot!.Category)
            {
                Metrics = run.Metrics,
                TotalScore = SelectedMetricScore(run),
                PassRate = SelectedMetricPassRate(run),
            };
        }).ToList();

    // Historical runs have no Score. Never infer a score from passed/failed or a display string.
    private static double? ValidScore(VerifyMetricResult metric) =>
        metric.Score is { } score && double.IsFinite(score) && score is >= 0 and <= 100 ? score : null;

    private static double? SelectedMetricScore(VerifyRunResponse run) =>
        run.Metrics.Count == 0 || run.Metrics.Any(metric => ValidScore(metric) is null) ? null
            : Math.Round(run.Metrics.Average(metric => ValidScore(metric)!.Value), 2);

    private static double? SelectedMetricPassRate(VerifyRunResponse run)
    {
        var items = run.EvaluationSnapshot!.Indicators.SelectMany(section => section.Children)
            .SelectMany(child => child.Items).ToList();
        // Every selected metric needs a recorded acceptance rule and a definite outcome
        // supported by this run. Neither scoring bands nor legacy/configuration flags suffice.
        if (run.Metrics.Count == 0) return null;
        foreach (var metric in run.Metrics)
        {
            var item = items.FirstOrDefault(item => item.MetricId == metric.MetricId);
            if (metric.Status is not ("passed" or "failed") || metric.ExecutionStatus != "completed"
                || metric.MeasurementSource != "current-run" || metric.Measurement is not { } measurement
                || item?.Automation is not { PassRule: not null } rule
                || !EvaluationAutomationRules.TryConvert(measurement, metric.Unit, rule.Unit, out var value)
                || EvaluationAutomationRules.Judge(rule, value) is not { } passed
                || passed != (metric.Status == "passed")) return null;
        }
        return Math.Round(100d * run.Metrics.Count(metric => metric.Status == "passed") / run.Metrics.Count,
            1, MidpointRounding.AwayFromZero);
    }

    private static string RunDate(VerifyRunResponse run) =>
        DateTimeOffset.TryParse(run.CompletedAt, out var completed) ? completed.ToString("yyyy-MM-dd") : "";

    // A long stability run can exceed the editable remark/Excel cell limit. Keep the measurement
    // summary, scoring/source explanation and the first/last evidence; never mutate the full task run.
    private static string SummarizeRunEvidence(string text)
    {
        if (text.Length <= MaxTextCharacters) return text;
        const string notice = "\n【采样证据过长，知识库仅保留首尾摘要；请在再次运行任务前导出本轮任务报告，留存完整证据。】\n";
        var prefixLength = MaxTextCharacters * 2 / 3;
        if (char.IsHighSurrogate(text[prefixLength - 1])) prefixLength--;
        var suffixStart = text.Length - (MaxTextCharacters - prefixLength - notice.Length);
        if (char.IsLowSurrogate(text[suffixStart])) suffixStart++;
        return text[..prefixLength] + notice + text[suffixStart..];
    }

    public KnowledgeRecord TaskDraft(string taskId, string? category = null)
    {
        var task = _tasks.ReadAll().FirstOrDefault(task => task.Id == taskId)
            ?? throw new KeyNotFoundException("验证任务不存在。");
        var run = ParseRun(task);
        if (!CanImport(run)) throw new ArgumentException("该任务缺少可同步的实测结果或历史快照，请重新执行任务。");
        var snapshot = run!.EvaluationSnapshot!;
        EvaluationIndicatorStore.Validate(snapshot);
        category ??= snapshot.Category;
        if (category != snapshot.Category) throw new ArgumentException("所选评价类别与任务运行时的指标类别不一致。");
        var machine = run.MachineSnapshot!;
        var items = snapshot.Indicators.SelectMany(section => section.Children).SelectMany(child => child.Items).ToList();
        var results = new Dictionary<string, string>();
        var remarks = new Dictionary<string, string>();
        var scores = items.ToDictionary(item => item.Id, _ => (double?)null);
        foreach (var item in items.Where(item => !string.IsNullOrEmpty(item.MetricId)))
        {
            var measured = run.Metrics.FirstOrDefault(metric => metric.MetricId == item.MetricId);
            if (measured is null) continue;
            scores[item.Id] = ValidScore(measured);
            results[item.Id] = measured.Value;
            remarks[item.Id] = SummarizeRunEvidence(string.Join("\n", new[]
                {
                    measured.Detail,
                    string.IsNullOrWhiteSpace(measured.ScoreReason) ? null : $"评分说明：{measured.ScoreReason}",
                    string.IsNullOrWhiteSpace(measured.MeasurementSource) ? null : $"测量来源：{measured.MeasurementSource}",
                }.Concat(measured.Evidence).Where(value => !string.IsNullOrWhiteSpace(value))));
        }
        return new KnowledgeRecord
        {
            MachineName = machine.Name, MachineNo = machine.DeviceCode, MachineModel = machine.Model,
            ControlSystem = machine.ControlSystem, TestDate = RunDate(run), Category = category, Snapshot = snapshot,
            DataSource = "sync", SyncTaskId = task.Id, SyncRunId = run.RunId,
            CategoryWeights = snapshot.Indicators.ToDictionary(section => section.Id, section => section.Weight),
            SubCategoryWeights = snapshot.Indicators.SelectMany(section => section.Children).ToDictionary(child => child.Id, child => child.Weight),
            Weights = items.ToDictionary(item => item.Id, item => item.Weight),
            Scores = scores, TestResults = results, Remarks = remarks,
        };
    }
}
