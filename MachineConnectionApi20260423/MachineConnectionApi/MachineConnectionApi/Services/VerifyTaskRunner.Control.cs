namespace MachineConnectionApi.Services;

using System.Diagnostics;
using System.Text.Json;
using MachineConnectionApi.Models;

public sealed record VerifyTaskControlResult(VerifyTaskDto Task, bool Applied, string Status, double ResponseMs);

public sealed partial class VerifyTaskRunner
{
    public static bool IsActive(string status) => status is "running" or "pausing" or "paused" or "stopping";

    public async Task<VerifyTaskControlResult?> ControlAsync(string taskId, string runId, string action, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        if (action is not ("pause" or "resume" or "stop")) throw new ArgumentException("无效任务控制操作");
        if (!_executions.TryGetValue(taskId, out var execution))
        {
            if (_store.ReadAll().All(task => task.Id != taskId)) return null;
            throw new InvalidOperationException("任务没有可控制的本轮运行");
        }
        if (execution.Id != runId) throw new InvalidOperationException("运行标识已变化，请刷新后操作");
        Task settled;
        lock (execution.Gate)
        {
            if (execution.Finished) throw new InvalidOperationException("本轮运行已结束");
            settled = execution.Control.Request(action);
        }
        var expected = action switch { "pause" => "paused", "resume" => "running", _ => "stopped" };
        try { await settled.WaitAsync(TimeSpan.FromMilliseconds(750), ct); }
        catch (TimeoutException) { /* In-flight operations must quiesce before reporting applied. */ }
        var current = _store.ReadAll().Single(task => task.Id == taskId);
        var status = execution.Control.State;
        if (status == "finished") throw new InvalidOperationException("本轮任务已结束，控制操作未应用，请刷新结果");
        if (status == expected && current.Status != expected)
            throw new IOException("运行控制已收敛，但状态或结果尚未写入任务库，请恢复结果后重新查询。");
        return new(current, status == expected && current.Status == expected, status, Math.Round(started.Elapsed.TotalMilliseconds, 2));
    }

    private void PersistControl(Execution execution, string state)
    {
        lock (execution.Gate)
        {
            if (execution.Finished || execution.Control.State != state) return;
            var snapshot = JsonSerializer.Deserialize<VerifyRunResponse>(execution.Snapshot, JsonOptions) ?? new();
            snapshot.Status = state;
            snapshot.Detail = state switch
            {
                "pausing" => "正在取消在途测量并等待清理，尚未确认暂停。",
                "paused" => "已暂停；继续时重新测量当前指标，已完成指标保留。",
                "stopping" => "正在停止并等待在途操作清理，尚未确认结束。",
                _ => "继续执行；当前未完成指标将重新测量。",
            };
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            _store.Update(rows =>
            {
                var index = rows.FindIndex(task => task.Id == execution.Task.Id && task.ActiveRunId == execution.Id);
                if (index >= 0) rows[index] = rows[index] with { Status = state, CurrentRunJson = json };
                return 0;
            });
            execution.Snapshot = json;
            WriteActivity("operation", "任务控制", execution.Task.Name + "：" + state);
        }
    }
}
