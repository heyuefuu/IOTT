namespace MachineConnectionApi.Services;

/// <summary>Pause cancels the current measurement; resume repeats that measurement from a clean snapshot.</summary>
public sealed class VerifyTaskExecutionControl
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _active;
    private TaskCompletionSource _resume = Signal(completed: true);
    private TaskCompletionSource _settled = Signal(completed: true);
    private string _state = "running";
    public Action<string>? StateChanged { get; set; }
    public string State { get { lock (_gate) return _state; } }
    public bool StopRequested => _stop.IsCancellationRequested;

    private static TaskCompletionSource Signal(bool completed = false)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed) signal.SetResult();
        return signal;
    }

    public Task Request(string action)
    {
        string state;
        Task result;
        lock (_gate)
        {
            if (action == "pause" && _state is "pausing" or "paused") return _settled.Task;
            if (action == "stop" && _state == "stopping") return _settled.Task;
            if (action == "resume" && _state == "running") return Task.CompletedTask;
            if (action == "pause" && _state == "running")
            {
                _state = "pausing";
                _resume = Signal();
                _settled = Signal();
            }
            else if (action == "resume" && _state == "paused")
            {
                _state = "running";
                _resume.TrySetResult();
                _settled.TrySetResult();
                _settled = Signal(completed: true);
            }
            else if (action == "stop" && _state is "running" or "pausing" or "paused")
            {
                _state = "stopping";
                _settled.TrySetResult();
                _settled = Signal();
                _resume.TrySetResult();
                _ = _stop.CancelAsync();
            }
            else throw new InvalidOperationException($"任务状态 {_state} 不接受 {action} 操作");
            state = _state;
            if (state is "pausing" or "stopping") _ = _active?.CancelAsync();
            result = _settled.Task;
        }
        StateChanged?.Invoke(state);
        return result;
    }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        while (true)
        {
            stopped.Token.ThrowIfCancellationRequested();
            Task resume;
            TaskCompletionSource settled;
            bool paused;
            lock (_gate)
            {
                paused = _state == "pausing";
                if (paused) _state = "paused";
                resume = _resume.Task;
                settled = _settled;
            }
            if (paused)
            {
                StateChanged?.Invoke("paused");
                settled.TrySetResult();
            }
            await resume.WaitAsync(stopped.Token);
            using var active = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
            lock (_gate)
            {
                if (_state != "running") continue;
                _active = active;
            }
            try
            {
                var result = await operation(active.Token);
                stopped.Token.ThrowIfCancellationRequested();
                if (State is "pausing" or "paused") continue;
                return result;
            }
            catch (OperationCanceledException) when (!stopped.IsCancellationRequested && State is "pausing" or "paused") { }
            finally { lock (_gate) if (ReferenceEquals(_active, active)) _active = null; }
        }
    }

    public void Finish(string state)
    {
        lock (_gate)
        {
            _state = state;
            _resume.TrySetResult();
            _settled.TrySetResult();
        }
    }
}
