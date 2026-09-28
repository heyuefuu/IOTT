namespace MachineConnectionApi.Services;

using System.Collections.Concurrent;
using System.Text.Json;
using MachineConnectionApi.Models;

public interface IVerifyTaskRunner
{
    /// <summary>执行验证任务并把结果（含完整 VerifyRunResponse JSON）留痕到任务记录；trigger 用于审计标注（手动/定时）。</summary>
    Task<VerifyTaskDto?> RunTaskAsync(string taskId, string trigger, CancellationToken ct);
    /// <summary>Reserve immediately and execute independently of the HTTP request lifetime.</summary>
    Task<VerifyTaskDto?> StartTaskAsync(string taskId, string trigger, CancellationToken ct);
    /// <summary>Replay completed results whose final task-store write failed; never repeat measurements.</summary>
    Task RetryPendingResultsAsync(CancellationToken ct);
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
    private readonly IDeviceStore? _devices;
    private readonly CancellationToken _shutdown;
    private readonly ConcurrentDictionary<string, byte> _runningTasks = new();
    private readonly VerifyExecutionLeaseService _leases;
    private readonly VerifyTaskCompletionJournal? _journal;
    private readonly ConcurrentDictionary<string, VerifyTaskPendingCompletion> _pending = new();
    private readonly SemaphoreSlim _completionGate = new(1, 1);

    private sealed class Execution(VerifyTaskDto task, string? deviceId, string id, DateTimeOffset started)
    {
        public VerifyTaskDto Task { get; } = task;
        public string? DeviceId { get; } = deviceId;
        public string Id { get; } = id;
        public DateTimeOffset Started { get; } = started;
        public object Gate { get; } = new();
        public bool Finished { get; set; }
        public string Snapshot { get; set; } = "";
        public double Progress { get; set; }
        public VerifyTaskDto Accepted { get; set; } = task;
        public Exception? SelectionError { get; set; }
        public IDisposable? Lease { get; set; }
    }

    public VerifyTaskRunner(
        IVerifyTaskStore store,
        IVerifyAutomationService verifyService,
        ISystemActivityLog activityLog,
        IConfiguration? configuration = null,
        IDeviceStore? devices = null,
        IHostApplicationLifetime? lifetime = null,
        VerifyExecutionLeaseService? leases = null,
        VerifyTaskCompletionJournal? completionJournal = null)
    {
        _store = store;
        _verifyService = verifyService;
        _activityLog = activityLog;
        _devices = devices;
        _shutdown = lifetime?.ApplicationStopping ?? CancellationToken.None;
        _leases = leases ?? new VerifyExecutionLeaseService(configuration, devices);
        _journal = completionJournal;
    }

    public async Task<VerifyTaskDto?> RunTaskAsync(string taskId, string trigger, CancellationToken ct)
    {
        var execution = await BeginAsync(taskId, trigger, ct);
        return execution is null ? null : await ExecuteAndReleaseAsync(execution, trigger, ct);
    }

    public async Task<VerifyTaskDto?> StartTaskAsync(string taskId, string trigger, CancellationToken ct)
    {
        var execution = await BeginAsync(taskId, trigger, ct);
        if (execution is null) return null;
        var accepted = execution.Accepted;
        // The runner and its dependencies are singletons. Do not use RequestAborted here: closing a
        // browser must not abort a 30-minute stability test. Shutdown is recovered on next startup.
        _ = Task.Run(async () =>
        {
            try { await ExecuteAndReleaseAsync(execution, trigger, _shutdown); }
            catch (Exception ex) { WriteActivity("error", trigger, execution.Task.Name + "：" + ex.Message); }
        });
        return accepted;
    }

    private async Task<Execution?> BeginAsync(string taskId, string trigger, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_runningTasks.TryAdd(taskId, 0)) throw new VerifyTaskAlreadyRunningException();
        IDisposable? lease = null;
        try
        {
            // Recover a completed run before considering a retry. This is persistence-only: no
            // device call, and an unrecovered result is never overwritten by a new run.
            await RetryPendingResultsAsync(ct);
            var original = _store.ReadAll().SingleOrDefault(row => row.Id == taskId);
            if (original is null) { _runningTasks.TryRemove(taskId, out _); return null; }
            if (original.Status == "running") throw new VerifyTaskAlreadyRunningException();
            var now = DateTimeOffset.Now;
            if (trigger == "定时" && original.LastAutoRunAt is { } previousAutoRun && previousAutoRun.ToOffset(now.Offset).Date >= now.Date)
            {
                _runningTasks.TryRemove(taskId, out _);
                return null; // A stale/overlapping scan must not repeat an already accepted day.
            }
            var originalJson = JsonSerializer.Serialize(original, JsonOptions);
            var current = JsonSerializer.Deserialize<VerifyTaskDto>(originalJson, JsonOptions)!;
            string? deviceId = null;
            Exception? error = null;
            try
            {
                deviceId = ResolveDeviceId(current);
                VerifyTaskOptionsValidation.Validate(current.Options, _devices);
                lease = await _leases.AcquireAsync(deviceId, current.Options, ct, allowIdOnlyReservation: _devices is null);
            }
            catch (ArgumentException ex) { error = ex; }
            ct.ThrowIfCancellationRequested();
            var value = new Execution(current, deviceId, Guid.NewGuid().ToString("N"), DateTimeOffset.Now)
            { SelectionError = error, Lease = lease };
            var initial = new VerifyRunResponse
            {
                RunId = value.Id, TaskId = current.Id, TaskName = current.Name, DeviceId = deviceId,
                Status = "running", StartedAt = value.Started.ToString("O"), TotalMetricCount = current.MetricIds.Count,
                OptionsSnapshot = current.Options ?? new VerifyRunOptions(),
                Metrics = current.MetricIds.Select(id => new VerifyMetricResult
                { MetricId = id, Name = id, Status = "unrated", ScoreReason = "等待本轮实测" }).ToList(),
            };
            value.Snapshot = JsonSerializer.Serialize(initial, JsonOptions);
            var accepted = _store.Update<VerifyTaskDto?>(rows =>
            {
                var index = rows.FindIndex(x => x.Id == taskId);
                if (index < 0) return null;
                if (rows[index].Status == "running") throw new VerifyTaskAlreadyRunningException();
                // Resolving the upstream registry is async: an edit during that await must invalidate
                // this reservation, not run the new configuration with the old target keys.
                if (JsonSerializer.Serialize(rows[index], JsonOptions) != originalJson)
                    throw new VerifyTaskCapacityException("任务配置在目标预检期间发生变化，请重新执行");
                return rows[index] = current with
                {
                    Status = "running", CompletedAt = null, ExecutionTime = "", Result = "", Detail = "",
                    ActiveRunId = value.Id, CurrentRunJson = value.Snapshot,
                    // Consume the day at acceptance, not completion (which may be tomorrow).
                    LastAutoRunAt = trigger == "定时" ? value.Started : current.LastAutoRunAt,
                };
            });
            if (accepted is null)
            {
                lease?.Dispose();
                _runningTasks.TryRemove(taskId, out _);
                return null;
            }
            value.Accepted = accepted;
            return value;
        }
        catch
        {
            lease?.Dispose();
            _runningTasks.TryRemove(taskId, out _);
            throw;
        }
    }

    private async Task<VerifyTaskDto?> ExecuteAndReleaseAsync(Execution execution, string trigger, CancellationToken ct)
    {
        try
        {
            VerifyRunResponse response;
            try
            {
                if (execution.SelectionError is not null) throw execution.SelectionError;
                response = await _verifyService.RunAsync(new VerifyRunRequest
                {
                    TaskId = execution.Task.Id, TaskName = execution.Task.Name, DeviceId = execution.DeviceId,
                    EvaluationCategory = execution.Task.EvaluationCategory, MetricIds = execution.Task.MetricIds,
                    Options = execution.Task.Options is null ? new VerifyRunOptions()
                        : JsonSerializer.Deserialize<VerifyRunOptions>(JsonSerializer.Serialize(execution.Task.Options, JsonOptions), JsonOptions),
                    ReportProgress = snapshot => PersistProgress(execution, snapshot),
                }, ct);
            }
            catch (Exception ex)
            {
                lock (execution.Gate)
                {
                    response = JsonSerializer.Deserialize<VerifyRunResponse>(execution.Snapshot, JsonOptions) ?? new();
                    Interrupt(response, ex is OperationCanceledException ? "执行中断" : "执行失败", ex.Message);
                }
            }
            return await CompleteAsync(execution, response, trigger);
        }
        finally
        {
            lock (execution.Gate) execution.Finished = true;
            execution.Lease?.Dispose();
            _runningTasks.TryRemove(execution.Task.Id, out _);
        }
    }

    private void PersistProgress(Execution execution, VerifyRunResponse snapshot)
    {
        lock (execution.Gate)
        {
            if (execution.Finished) return;
            // Serialize synchronously; never retain the engine's mutable response object.
            try
            {
                if (!double.IsFinite(snapshot.ProgressPercent) || snapshot.ProgressPercent < execution.Progress) return;
                var owned = JsonSerializer.Deserialize<VerifyRunResponse>(JsonSerializer.Serialize(snapshot, JsonOptions), JsonOptions)!;
                owned.RunId = execution.Id;
                owned.TaskId = execution.Task.Id;
                owned.TaskName = execution.Task.Name;
                owned.OptionsSnapshot = execution.Task.Options ?? new VerifyRunOptions();
                owned.ProgressPercent = Math.Clamp(owned.ProgressPercent, 0, 100);
                var json = JsonSerializer.Serialize(owned, JsonOptions);
                execution.Snapshot = json;
                execution.Progress = owned.ProgressPercent;
                _store.Update(rows =>
                {
                    var index = rows.FindIndex(x => x.Id == execution.Task.Id && x.ActiveRunId == execution.Id && x.Status == "running");
                    if (index >= 0) rows[index] = rows[index] with { CurrentRunJson = json };
                    return 0;
                });
            }
            catch (Exception ex)
            {
                // A transient progress-store failure must not turn successful device communication
                // into a fake test failure. Final persistence still reports its own failure normally.
                WriteActivity("warning", "进度", execution.Task.Name + "：进度快照保存失败：" + ex.Message);
            }
        }
    }

    private async Task<VerifyTaskDto?> CompleteAsync(Execution execution, VerifyRunResponse response, string trigger)
    {
        VerifyTaskPendingCompletion pending;
        lock (execution.Gate)
        {
            execution.Finished = true;
            var completed = DateTimeOffset.Now;
            response.TaskId = execution.Task.Id;
            response.RunId = execution.Id;
            response.TaskName = execution.Task.Name;
            response.OptionsSnapshot = execution.Task.Options ?? new VerifyRunOptions();
            if (response.Status is not ("completed" or "failed"))
                Interrupt(response, "执行失败", "验证服务未返回已结束的运行结果");
            if (string.IsNullOrWhiteSpace(response.StartedAt)) response.StartedAt = execution.Started.ToString("O");
            if (string.IsNullOrWhiteSpace(response.CompletedAt)) response.CompletedAt = completed.ToString("O");
            pending = new VerifyTaskPendingCompletion(execution.Id, trigger, execution.Accepted with
            {
                Status = response.Status, CompletedAt = completed,
                ExecutionTime = FormatDuration(completed - execution.Started),
                Result = response.Result, Detail = response.Detail, LastRunJson = JsonSerializer.Serialize(response, JsonOptions),
                CurrentRunJson = "", ActiveRunId = null,
            });
            // Keep the complete, detached result even when both disk writes fail. A same-process
            // retry or the periodic recovery pass replays this value instead of repeating the test.
            _pending[execution.Task.Id] = pending;
        }
        await _completionGate.WaitAsync(CancellationToken.None);
        try { return await PersistCompletionAsync(pending); }
        finally { _completionGate.Release(); }
    }

    public async Task RetryPendingResultsAsync(CancellationToken ct)
    {
        await _completionGate.WaitAsync(ct);
        try
        {
            if (_journal is not null)
                foreach (var item in _journal.ReadAll()) _pending.TryAdd(item.Task.Id, item);
            foreach (var item in _pending.Values.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                await PersistCompletionAsync(item);
            }
        }
        finally { _completionGate.Release(); }
    }

    private async Task<VerifyTaskDto?> PersistCompletionAsync(VerifyTaskPendingCompletion pending)
    {
        // Independent write-ahead file: if verify-tasks.json's atomic rename is unavailable, a
        // restart can still recover the FULL result before closing any abandoned running rows.
        Exception? journalError = null;
        if (_journal is not null)
        {
            try { await RetryWriteAsync(() => { _journal.Save(pending); return 0; }); }
            catch (Exception ex) when (IsPersistenceError(ex)) { journalError = ex; }
        }
        VerifyTaskDto? updated;
        try
        {
            updated = await RetryWriteAsync(() => _store.Update<VerifyTaskDto?>(rows =>
            {
                var index = rows.FindIndex(row => row.Id == pending.Task.Id);
                if (index < 0) return null;
                var row = rows[index];
                // Fencing token: a delayed replay must never clear or overwrite a newer run.
                if (row.ActiveRunId != pending.RunId)
                    return row.ActiveRunId is null && row.LastRunJson == pending.Task.LastRunJson ? row : null;
                return rows[index] = row with
                {
                    Status = pending.Task.Status, CompletedAt = pending.Task.CompletedAt,
                    ExecutionTime = pending.Task.ExecutionTime, Result = pending.Task.Result,
                    Detail = pending.Task.Detail, LastRunJson = pending.Task.LastRunJson,
                    CurrentRunJson = "", ActiveRunId = null, LastAutoRunAt = pending.Task.LastAutoRunAt,
                };
            }));
        }
        catch (Exception ex) when (IsPersistenceError(ex))
        {
            var detail = journalError is null && _journal is not null ? "完整结果已保存在恢复日志"
                : "完整结果仅保留在本进程内存；恢复磁盘前请勿重启服务";
            WriteActivity("error", pending.Trigger, $"{pending.Task.Name}：结果落盘失败；{detail}；{ex.Message}");
            throw new VerifyTaskPersistenceException(pending.Task.Id, pending.RunId, detail, ex);
        }
        _pending.TryRemove(pending.Task.Id, out _);
        if (_journal is not null)
        {
            try { await RetryWriteAsync(() => { _journal.Remove(pending.Task.Id, pending.RunId); return 0; }); }
            catch (Exception ex) when (IsPersistenceError(ex))
            { WriteActivity("warning", "恢复", "任务结果已保存，恢复日志稍后清理：" + ex.Message); }
        }
        WriteActivity(updated?.Status == "completed" ? "operation" : "warning", pending.Trigger,
            updated is null ? $"{pending.Task.Name}：旧运行 {pending.RunId} 的恢复记录与当前运行不匹配，未覆盖当前结果"
                : $"{pending.Task.Name}：{pending.Task.Result}，{pending.Task.Detail}");
        return updated;
    }

    private static bool IsPersistenceError(Exception error) => error is IOException or UnauthorizedAccessException;

    private static async Task<T> RetryWriteAsync<T>(Func<T> write)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return write(); }
            catch (Exception ex) when (IsPersistenceError(ex) && attempt < 2)
            { await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), CancellationToken.None); }
        }
    }

    internal static void Interrupt(VerifyRunResponse response, string result, string detail)
    {
        response.Status = "failed";
        response.Result = result;
        response.Detail = detail;
        response.CompletedAt = DateTimeOffset.Now.ToString("O");
        response.TotalScore = null;
        response.ScoreSummary = "所选测试项均分，非综合评价分；本轮执行中断，总分待评分";
        response.CurrentMetricId = null;
        foreach (var metric in response.Metrics.Where(x => x.ExecutionStatus is "pending" or "running"))
        {
            metric.ExecutionStatus = metric.ExecutionStatus == "running" ? "error" : "skipped";
            metric.Status = metric.ExecutionStatus == "error" ? "error" : "unrated";
            metric.Score = null;
            metric.ScoreReason = result;
            metric.Result = result;
        }
        response.CompletedMetricCount = response.Metrics.Count(x => x.ExecutionStatus is "completed" or "error" or "skipped");
        response.TotalMetricCount = response.Metrics.Count;
        // Keep the last measured percentage: an interrupted 30-minute sample is not 100% measured.
    }

    private void WriteActivity(string type, string trigger, string detail)
    {
        try { _activityLog.Write(type, $"执行验证任务（{trigger}）", detail); }
        catch { /* Activity logging cannot retain an execution reservation or overwrite a result. */ }
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

public sealed class VerifyTaskPersistenceException(string taskId, string runId, string detail, Exception inner)
    : IOException("执行已结束但结果暂未写入任务库；" + detail + "；请重试结果恢复，不会重新测试设备。", inner)
{
    public string TaskId { get; } = taskId;
    public string RunId { get; } = runId;
}

public sealed record VerifyTaskPendingCompletion(string RunId, string Trigger, VerifyTaskDto Task);

/// <summary>Independent atomic recovery journal, replayed before stale-running startup recovery.</summary>
public sealed class VerifyTaskCompletionJournal
{
    private readonly JsonFileStore<VerifyTaskPendingCompletion> _store;
    public VerifyTaskCompletionJournal(string? fileName = null) =>
        _store = new(fileName ?? "verify-task-completions.json");
    public List<VerifyTaskPendingCompletion> ReadAll() => _store.ReadAll();
    public void Save(VerifyTaskPendingCompletion value) => _store.Update(rows =>
    {
        rows.RemoveAll(row => row.Task.Id == value.Task.Id && row.RunId == value.RunId);
        rows.Add(value);
        return 0;
    });
    public void Remove(string taskId, string runId) => _store.Update(rows =>
        rows.RemoveAll(row => row.Task.Id == taskId && row.RunId == runId));
}

public static class VerifyTaskOptionsValidation
{
    public static void Validate(VerifyRunOptions? options, IDeviceStore? devices = null)
    {
        if (options is null) return;
        if (options.ProbeTimeoutMs is < 500 or > 30000 || options.CommunicationRounds is < 1 or > 100
            || options.MaxParallelTargets is < 1 or > 100 || options.RequiredMinConcurrentSuccess is < 0 or > 100)
            throw new ArgumentException("测试参数超出安全范围：超时500–30000ms，轮数/并发上限1–100。");
        var ids = options.ConcurrentDeviceIds;
        if (ids is null) return;
        if (ids.Count > 100 || ids.Any(id => string.IsNullOrWhiteSpace(id) || id != id.Trim() || id.Length > 200)
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new ArgumentException("并发设备目标必须为不重复的有效设备ID，最多100个。");
        if (devices is null) return;
        var all = devices.ReadAll();
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var matching = all.Where(device => device.Id == id).ToArray();
            if (matching.Length != 1) throw new ArgumentException($"并发设备 {id} 不存在或标识不唯一。");
            var device = matching[0];
            if (string.IsNullOrWhiteSpace(device.Host) || device.Port is < 1 or > 65535
                || !endpoints.Add($"{device.Host.Trim()}:{device.Port}"))
                throw new ArgumentException("并发设备必须具有有效且互不重复的网络端点。");
        }
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

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Replay full completed results BEFORE interrupting abandoned runs. If replay cannot be
        // persisted, fail startup instead of destroying recoverable evidence or accepting a new run.
        await _runner.RetryPendingResultsAsync(cancellationToken);
        RecoverStaleRunningTasks();
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                    const string detail = "服务重启时任务仍处于运行状态，已自动结束为中断；请重新执行";
                    var previous = rows[index];
                    var lastRun = previous.LastRunJson;
                    try
                    {
                        // Only close this run's snapshot. Never relabel the previous completed run.
                        var snapshot = string.IsNullOrWhiteSpace(previous.CurrentRunJson) ? null
                            : JsonSerializer.Deserialize<VerifyRunResponse>(previous.CurrentRunJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                        if (snapshot is not null && (string.IsNullOrWhiteSpace(previous.ActiveRunId) || snapshot.RunId == previous.ActiveRunId))
                        {
                            VerifyTaskRunner.Interrupt(snapshot, "执行中断", detail);
                            lastRun = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                        }
                    }
                    catch (JsonException) { /* Preserve the last valid historical run if recovery data is damaged. */ }
                    rows[index] = previous with
                    {
                        Status = "failed", Result = "执行中断", Detail = detail,
                        CompletedAt = DateTimeOffset.Now, CurrentRunJson = "", ActiveRunId = null,
                        LastRunJson = lastRun,
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
        await _runner.RetryPendingResultsAsync(ct);
        var now = DateTimeOffset.Now;
        var dueTasks = _store.ReadAll().Where(task => IsDue(task, now)).ToArray();
        foreach (var task in dueTasks)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("定时执行验证任务 {TaskName}（{Time}）", task.Name, task.ScheduleTime);
            try
            {
                // Wait only for validation/reservation, never for the 30-minute measurement. A
                // later scan can use a slot freed by short B even while long A is still running.
                await _runner.StartTaskAsync(task.Id, "定时", ct);
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
        }
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
        // Once per local calendar day, including interrupted accepted runs after restart. Moving
        // ScheduleTime later in the same day must not trigger the already-consumed task twice.
        return task.LastAutoRunAt is not { } last || last.ToOffset(now.Offset).Date < now.Date;
    }
}
