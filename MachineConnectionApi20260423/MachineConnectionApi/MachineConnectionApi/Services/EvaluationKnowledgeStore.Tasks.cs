using System.Text.Json;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

public sealed partial class EvaluationKnowledgeStore
{
    private static VerifyRunResponse? ParseRun(VerifyTaskDto task)
    {
        if (string.IsNullOrWhiteSpace(task.LastRunJson)) return null;
        try { return JsonSerializer.Deserialize<VerifyRunResponse>(task.LastRunJson, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static bool CanImport(VerifyRunResponse? run) =>
        run is { Metrics.Count: > 0, MachineSnapshot: not null, EvaluationSnapshot: not null }
        && !string.IsNullOrWhiteSpace(run.RunId);

    public List<KnowledgeTask> ListTasks() => _tasks.ReadAll()
        .Select(task => (Task: task, Run: ParseRun(task)))
        .Where(pair => CanImport(pair.Run))
        .Select(pair =>
        {
            var run = pair.Run!;
            var machine = run.MachineSnapshot!;
            return new KnowledgeTask(pair.Task.Id, pair.Task.Name, machine.Name, machine.DeviceCode,
                machine.Model, machine.ControlSystem, RunDate(run), run.Status, run.Result, run.EvaluationSnapshot!.Category);
        }).ToList();

    private static string RunDate(VerifyRunResponse run) =>
        DateTimeOffset.TryParse(run.CompletedAt, out var completed) ? completed.ToString("yyyy-MM-dd") : "";

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
        foreach (var item in items.Where(item => !string.IsNullOrEmpty(item.MetricId)))
        {
            var measured = run.Metrics.FirstOrDefault(metric => metric.MetricId == item.MetricId);
            if (measured is null) continue;
            results[item.Id] = measured.Value;
            remarks[item.Id] = string.Join("\n", new[] { measured.Detail }.Concat(measured.Evidence)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        return new KnowledgeRecord
        {
            MachineName = machine.Name, MachineNo = machine.DeviceCode, MachineModel = machine.Model,
            ControlSystem = machine.ControlSystem, TestDate = RunDate(run), Category = category, Snapshot = snapshot,
            DataSource = "sync", SyncTaskId = task.Id, SyncRunId = run.RunId,
            CategoryWeights = snapshot.Indicators.ToDictionary(section => section.Id, section => section.Weight),
            SubCategoryWeights = snapshot.Indicators.SelectMany(section => section.Children).ToDictionary(child => child.Id, child => child.Weight),
            Weights = items.ToDictionary(item => item.Id, item => item.Weight),
            Scores = items.ToDictionary(item => item.Id, _ => (double?)null), TestResults = results, Remarks = remarks,
        };
    }
}
