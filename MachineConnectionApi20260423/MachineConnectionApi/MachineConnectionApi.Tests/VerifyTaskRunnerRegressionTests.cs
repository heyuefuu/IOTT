namespace MachineConnectionApi.Tests;

using MachineConnectionApi.Models;
using MachineConnectionApi.Services;

internal static class VerifyTaskRunnerRegressionTests
{
    public static async Task ExecutionFailureReleasesTaskForRetry()
    {
        var task = new VerifyTaskDto
        {
            Id = "task-1",
            Name = "机床实时验证",
            Type = "综合验证",
            Status = "pending",
            CreatedAt = DateTimeOffset.Now,
            MetricIds = ["file-integrity"],
        };
        var store = new MemoryTaskStore(task);
        var runner = new VerifyTaskRunner(store, new FailingVerifyService(), new NullActivityLog());

        var result = await runner.RunTaskAsync(task.Id, "手动", CancellationToken.None);

        AssertEqual("failed", result?.Status, "ReturnedStatus");
        AssertEqual("failed", store.ReadAll().Single().Status, "PersistedStatus");
        if (!store.ReadAll().Single().Detail.Contains("机床连接中断", StringComparison.Ordinal))
            throw new InvalidOperationException("Failure detail was not persisted");
    }

    public static async Task ConcurrentExecutionIsRejected()
    {
        var task = new VerifyTaskDto
        {
            Id = "task-2",
            Name = "并发保护验证",
            Type = "综合验证",
            CreatedAt = DateTimeOffset.Now,
        };
        var store = new MemoryTaskStore(task);
        var service = new BlockingVerifyService();
        var runner = new VerifyTaskRunner(store, service, new NullActivityLog());
        var firstRun = runner.RunTaskAsync(task.Id, "手动", CancellationToken.None);
        await service.Started.Task;

        var duplicateRejected = false;
        try { await runner.RunTaskAsync(task.Id, "手动", CancellationToken.None); }
        catch (InvalidOperationException) { duplicateRejected = true; }
        service.Release.TrySetResult();
        await firstRun;

        AssertEqual(true, duplicateRejected, "DuplicateRejected");
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
    }

    private sealed class MemoryTaskStore(params VerifyTaskDto[] tasks) : IVerifyTaskStore
    {
        private readonly object _gate = new();
        private List<VerifyTaskDto> _tasks = [.. tasks];
        public List<VerifyTaskDto> ReadAll() { lock (_gate) return [.. _tasks]; }
        public void WriteAll(IEnumerable<VerifyTaskDto> items)
        {
            lock (_gate) _tasks = [.. items];
        }
        public TResult Update<TResult>(Func<List<VerifyTaskDto>, TResult> update)
        {
            lock (_gate)
            {
                var rows = _tasks.ToList();
                var result = update(rows);
                _tasks = rows;
                return result;
            }
        }
    }

    private sealed class FailingVerifyService : IVerifyAutomationService
    {
        public Task<VerifyRunResponse> RunAsync(VerifyRunRequest request, CancellationToken ct) =>
            Task.FromException<VerifyRunResponse>(new InvalidOperationException("机床连接中断"));
    }

    private sealed class BlockingVerifyService : IVerifyAutomationService
    {
        private int _calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<VerifyRunResponse> RunAsync(VerifyRunRequest request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return new VerifyRunResponse
            {
                TaskId = request.TaskId,
                TaskName = request.TaskName ?? "",
                Status = "completed",
                Result = "通过",
                Detail = "完成",
            };
        }
    }

    private sealed class NullActivityLog : ISystemActivityLog
    {
        public List<SystemLogDto> ReadAll() => [];
        public SystemLogDto Append(SystemLogDto entry) => entry;
        public void Write(string type, string action, string detail, string user = "系统", string ip = "-") { }
    }
}
