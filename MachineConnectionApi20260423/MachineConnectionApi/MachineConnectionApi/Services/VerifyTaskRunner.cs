namespace MachineConnectionApi.Services;

using System.Collections.Concurrent;
using System.Text.Json;
using MachineConnectionApi.Models;

public interface IVerifyTaskRunner
{
    /// <summary>执行验证任务并把结果（含完整 VerifyRunResponse JSON）留痕到任务记录；trigger 用于审计标注（手动/定时）。</summary>
    Task<VerifyTaskDto?> RunTaskAsync(string taskId, string trigger, CancellationToken ct);
}

/// <summary>同一任务已在执行中（进程内互斥），重复提交被拒绝。</summary>
public sealed class VerifyTaskAlreadyRunningException : InvalidOperationException
{
    public VerifyTaskAlreadyRunningException() : base("任务正在执行，请勿重复提交") { }
}

public sealed class VerifyTaskCapacityException(string message) : InvalidOperationException(message);

/// <summary>手动运行（控制器）与定时调度共用的任务执行入口。</summary>
public sealed class VerifyTaskRunner : IVerifyTaskRunner
{
    public const int MaxConcurrentTasks = 7;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IVerifyTaskStore _store;
    private readonly IVerifyAutomationService _verifyService;
    private readonly ISystemActivityLog _activityLog;
    private readonly ConcurrentDictionary<string, byte> _runningTasks = new();
    private readonly object _executionGate = new();
    private readonly Dictionary<string, string?> _activeDevices = new();
    private readonly int _maxConcurrentTasks;

    public VerifyTaskRunner(
        IVerifyTaskStore store,
        IVerifyAutomationService verifyService,
        ISystemActivityLog activityLog,
        IConfiguration? configuration = null)
    {
        _store = store;
        _verifyService = verifyService;
        _activityLog = activityLog;
        _maxConcurrentTasks = Math.Clamp(configuration?.GetValue<int?>("VerifyTasks:MaxConcurrentTasks")
            ?? MaxConcurrentTasks, 1, MaxConcurrentTasks);
    }

    public async Task<VerifyTaskDto?> RunTaskAsync(string taskId, string trigger, CancellationToken ct)
    {
        if (!_runningTasks.TryAdd(taskId, 0))
            throw new VerifyTaskAlreadyRunningException();
        try
        {
            return await RunTaskCoreAsync(taskId, trigger, ct);
        }
        finally
        {
            lock (_executionGate) _activeDevices.Remove(taskId);
            _runningTasks.TryRemove(taskId, out _);
        }
    }

    private async Task<VerifyTaskDto?> RunTaskCoreAsync(string taskId, string trigger, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var started = DateTimeOffset.Now;
        string? deviceId = null;
        ArgumentException? selectionError = null;
        var task = _store.Update<VerifyTaskDto?>(rows =>
        {
            var index = rows.FindIndex(x => x.Id == taskId);
            if (index < 0) return null;
            var current = rows[index];
            try { deviceId = ResolveDeviceId(current); }
            catch (ArgumentException ex) { selectionError = ex; return current; }
            ReserveExecution(taskId, deviceId);
            rows[index] = current with
            {
                Status = "running", CompletedAt = null, ExecutionTime = "", Result = "", Detail = "",
            };
            return current;
        });
        if (task is null) return null;
        if (selectionError is not null) return PersistFailure(task, started, trigger, selectionError);

        VerifyRunResponse response;
        try
        {
            response = await _verifyService.RunAsync(new VerifyRunRequest
            {
                TaskId = task.Id,
                TaskName = task.Name,
                DeviceId = deviceId,
                EvaluationCategory = task.EvaluationCategory,
                MetricIds = task.MetricIds,
            }, ct);
        }
        catch (Exception ex)
        {
            return PersistFailure(task, started, trigger, ex);
        }

        var completed = DateTimeOffset.Now;
        var updated = _store.Update<VerifyTaskDto?>(rows =>
        {
            var index = rows.FindIndex(x => x.Id == taskId);
            if (index < 0) return null;
            var value = rows[index] with
            {
                Status = response.Status,
                CompletedAt = completed,
                ExecutionTime = FormatDuration(completed - started),
                Result = response.Result,
                Detail = response.Detail,
                LastRunJson = JsonSerializer.Serialize(response, JsonOptions),
                LastAutoRunAt = trigger == "定时" ? completed : rows[index].LastAutoRunAt,
            };
            rows[index] = value;
            return value;
        });
        if (updated is null) return null;
        _activityLog.Write(
            response.Status == "completed" ? "operation" : "warning",
            $"执行验证任务（{trigger}）",
            $"{task.Name}：{response.Result}，{response.Detail}");
        return updated;
    }

    private VerifyTaskDto? PersistFailure(VerifyTaskDto task, DateTimeOffset started, string trigger, Exception ex)
    {
        var completed = DateTimeOffset.Now;
        var run = new VerifyRunResponse
        {
            RunId = Guid.NewGuid().ToString("N"),
            TaskId = task.Id,
            TaskName = task.Name,
            Status = "failed",
            Result = "执行失败",
            Detail = ex.Message,
            StartedAt = started.ToString("yyyy-MM-dd HH:mm:ss"),
            CompletedAt = completed.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        var updated = _store.Update<VerifyTaskDto?>(rows =>
        {
            var index = rows.FindIndex(x => x.Id == task.Id);
            if (index < 0) return null;
            var value = rows[index] with
            {
                Status = "failed",
                CompletedAt = completed,
                ExecutionTime = FormatDuration(completed - started),
                Result = run.Result,
                Detail = run.Detail,
                LastRunJson = JsonSerializer.Serialize(run, JsonOptions),
                LastAutoRunAt = trigger == "定时" ? completed : rows[index].LastAutoRunAt,
            };
            rows[index] = value;
            return value;
        });
        if (updated is null) return null;
        _activityLog.Write("error", $"执行验证任务（{trigger}）", $"{task.Name}：{ex.Message}");
        return updated;
    }

    private void ReserveExecution(string taskId, string? deviceId)
    {
        var selectedDevice = string.IsNullOrEmpty(deviceId) ? null : deviceId;
        lock (_executionGate)
        {
            if (_activeDevices.Count > 0 && (selectedDevice is null || _activeDevices.Values.Contains(null)))
                throw new VerifyTaskCapacityException("全设备验证需要独占执行，请等待当前任务完成后重试");
            if (_activeDevices.Values.Contains(selectedDevice))
                throw new VerifyTaskCapacityException("该机床已有验证任务正在执行，请等待完成后重试");
            if (_activeDevices.Count >= _maxConcurrentTasks)
                throw new VerifyTaskCapacityException($"同时执行的验证任务已达上限（{_maxConcurrentTasks} 个），请稍后重试");
            _activeDevices.Add(taskId, selectedDevice);
        }
    }

    private static string? ResolveDeviceId(VerifyTaskDto task)
    {
        var deviceId = task.DeviceId?.Trim();
        var machineId = task.MachineId?.Trim();
        if (!string.IsNullOrEmpty(deviceId) && !string.IsNullOrEmpty(machineId)
            && !string.Equals(deviceId, machineId, StringComparison.Ordinal))
            throw new ArgumentException("设备 ID 与机床 ID 不一致，请重新选择设备");
        return !string.IsNullOrEmpty(deviceId) ? deviceId : machineId;
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalSeconds < 1) return $"{value.TotalMilliseconds:N0} ms";
        if (value.TotalMinutes < 1) return $"{value.TotalSeconds:N2} s";
        return $"{(int)value.TotalMinutes}m {value.Seconds}s";
    }
}

/// <summary>
/// 验证任务定时调度：ScheduleType=daily 的任务在每天 ScheduleTime（HH:mm）自动执行一次。
/// 每 30 秒扫描一次任务库；同一天内不重复触发（以 LastAutoRunAt 判定）。
/// </summary>
public sealed class VerifyTaskSchedulerHostedService : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);

    private readonly IVerifyTaskStore _store;
    private readonly IVerifyTaskRunner _runner;
    private readonly ILogger<VerifyTaskSchedulerHostedService> _logger;

    public VerifyTaskSchedulerHostedService(
        IVerifyTaskStore store,
        IVerifyTaskRunner runner,
        ILogger<VerifyTaskSchedulerHostedService> logger)
    {
        _store = store;
        _runner = runner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RecoverStaleRunningTasks();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ScanInterval, stoppingToken);
                await RunDueTasksAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "验证任务定时调度扫描异常");
            }
        }
    }

    /// <summary>
    /// 进程崩溃/强杀会把 Status=running 留在任务库里，而 IsDue 会永久跳过 running 任务，
    /// 导致每日调度失效。启动时把遗留的 running 复位为 failed（执行状态本身不跨进程存活）。
    /// </summary>
    private void RecoverStaleRunningTasks()
    {
        try
        {
            var recovered = _store.Update(rows =>
            {
                var names = new List<string>();
                for (var index = 0; index < rows.Count; index++)
                {
                    if (rows[index].Status != "running") continue;
                    rows[index] = rows[index] with
                    {
                        Status = "failed",
                        Result = "执行中断",
                        Detail = "服务重启时任务仍处于运行状态，已自动复位；请重新执行",
                    };
                    names.Add(rows[index].Name);
                }
                return names;
            });
            if (recovered.Count > 0)
                _logger.LogWarning("已复位 {Count} 个遗留 running 状态的验证任务：{Names}",
                    recovered.Count, string.Join("、", recovered));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "复位遗留 running 验证任务失败");
        }
    }

    private async Task RunDueTasksAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var dueTasks = _store.ReadAll().Where(task => IsDue(task, now));
        await Task.WhenAll(dueTasks.Select(async task =>
        {
            _logger.LogInformation("定时执行验证任务 {TaskName}（{Time}）", task.Name, task.ScheduleTime);
            try
            {
                await _runner.RunTaskAsync(task.Id, "定时", ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is VerifyTaskCapacityException or VerifyTaskAlreadyRunningException)
            {
                _logger.LogDebug("定时验证任务 {TaskId} 等待下次扫描：{Reason}", task.Id, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "定时执行验证任务 {TaskId} 失败", task.Id);
            }
        }));
    }

    private static bool IsDue(VerifyTaskDto task, DateTimeOffset now)
    {
        if (!string.Equals(task.ScheduleType, "daily", StringComparison.OrdinalIgnoreCase))
            return false;
        if (task.Status == "running")
            return false;
        var parts = task.ScheduleTime.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var hour) || !int.TryParse(parts[1], out var minute))
            return false;
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
            return false;

        var scheduledToday = new DateTimeOffset(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);
        if (now < scheduledToday)
            return false;
        // 今天已自动跑过（或手动跑过之后调度点未到）则不再触发
        return task.LastAutoRunAt is not { } last || last < scheduledToday;
    }
}
