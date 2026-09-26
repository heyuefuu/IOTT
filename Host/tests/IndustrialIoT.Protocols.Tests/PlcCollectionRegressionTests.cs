using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Infrastructure.BackgroundServices;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Pipeline;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

internal static class PlcCollectionRegressionTests
{
    public static async Task CollectGroupsAsync()
    {
        using var fixture = new Fixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskId = await fixture.Scheduler.StartCollectionAsync(Profile(
            Group("Fast", 10, "HR0"), Group("Slow", 150, "C0")));
        try
        {
            var counts = new Dictionary<string, int> { ["Fast"] = 0, ["Slow"] = 0 };
            while (counts["Slow"] < 2 || counts["Fast"] < 3)
            {
                var batch = await fixture.Scheduler.GetOutputReader().ReadAsync(timeout.Token);
                var value = batch.Values.Single();
                TestSupport.Require(batch.DeviceId == "plc-fixture" && value.Quality == TagQuality.Good,
                    "PLC batches must retain the selected device and driver quality");
                TestSupport.Require(value.Address == (batch.GroupName == "Fast" ? "HR0" : "C0")
                    && Equals(value.Value, batch.GroupName == "Fast" ? (object)123 : true),
                    "Each collection group must forward its driver's actual input value");
                counts[batch.GroupName]++;
            }
            TestSupport.Require(counts["Fast"] > counts["Slow"],
                "Groups with different periods must collect independently at their configured rates");
        }
        finally { await fixture.Scheduler.StopCollectionAsync(taskId); }
        var stoppedCount = fixture.Driver.ReadCount;
        await Task.Delay(50);
        TestSupport.Require(fixture.Driver.ReadCount == stoppedCount
            && fixture.Scheduler.GetActiveTasksSnapshot().Count == 0,
            "Stopping collection must remove the task and prevent further reads");
    }
    public static async Task RequestCancellationAsync()
    {
        using var fixture = new Fixture();
        using var request = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var taskId = await fixture.Scheduler.StartCollectionAsync(Profile(Group("Fast", 10, "HR0")), request.Token);
        try
        {
            await fixture.Scheduler.GetOutputReader().ReadAsync(timeout.Token);
            request.Cancel();
            while (fixture.Scheduler.GetOutputReader().TryRead(out _)) { }
            await fixture.Scheduler.GetOutputReader().ReadAsync(timeout.Token);
            TestSupport.Require(fixture.Scheduler.GetActiveTasksSnapshot().ContainsKey(taskId),
                "An accepted persistent collection task must survive cancellation of its HTTP request");
        }
        finally { await fixture.Scheduler.StopCollectionAsync(taskId); }
    }

    public static async Task StopWaitsForReadAsync()
    {
        using var fixture = new Fixture();
        fixture.Driver.BlockRead = true;
        var taskId = await fixture.Scheduler.StartCollectionAsync(Profile(Group("Fast", 10, "HR0")));
        await fixture.Driver.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopping = fixture.Scheduler.StopCollectionAsync(taskId);
        try
        {
            await fixture.Driver.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            TestSupport.Require(!stopping.IsCompleted && fixture.Pool.ReleaseCount == 0,
                "Stop must await an in-flight read before releasing its driver");
        }
        finally
        {
            fixture.Driver.AllowReadExit.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        }
        TestSupport.Require(fixture.Pool.ReleaseCount == 1 && !fixture.Pool.ReleasedDuringRead,
            "The connection must be released exactly once, after the read finishes");
    }

    public static async Task GroupInitializationFailureAsync()
    {
        using var fixture = new Fixture();
        fixture.Pool.FailGetCall = 2;
        var taskId = await fixture.Scheduler.StartCollectionAsync(Profile(
            Group("Fast", 10, "HR0"), Group("Slow", 150, "C0")));
        await fixture.Pool.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Scheduler.StopCollectionAsync(taskId).WaitAsync(TimeSpan.FromSeconds(2));
        TestSupport.Require(fixture.Scheduler.GetActiveTasksSnapshot().Count == 0
            && fixture.Pool.ReleaseCount == 1,
            "A failed group initialization must cancel siblings and clean up the collection task");
    }

    public static async Task SharedDeviceLifecycleAsync()
    {
        using var fixture = new Fixture();
        var profile = Profile(Group("Fast", 10, "HR0"));
        var firstId = await fixture.Scheduler.StartCollectionAsync(profile);
        var secondId = await fixture.Scheduler.StartCollectionAsync(profile);
        await fixture.Scheduler.StopCollectionAsync(firstId);
        TestSupport.Require(fixture.Pool.ReleaseCount == 0,
            "Stopping one task must keep a shared device connection for the remaining task");
        fixture.Pool.BlockRelease = true;
        var stopping = fixture.Scheduler.StopCollectionAsync(secondId);
        await fixture.Pool.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var starting = fixture.Scheduler.StartCollectionAsync(profile);
        try
        {
            TestSupport.Require(!starting.IsCompleted,
                "A new task must wait for the old device connection release to complete");
        }
        finally { fixture.Pool.AllowRelease.TrySetResult(); }
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        var thirdId = await starting.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Scheduler.StopCollectionAsync(thirdId);
        TestSupport.Require(fixture.Pool.ReleaseCount == 2 && !fixture.Pool.ReleasedDuringRead,
            "Each shared connection lifetime must be released once after all reads finish");
    }

    private sealed class Fixture : IDisposable
    {
        public MemoryDriver Driver { get; } = new();
        public MemoryPool Pool { get; }
        public CollectionSchedulerService Scheduler { get; }
        private readonly ServiceProvider _services;
        public Fixture()
        {
            Pool = new(Driver);
            _services = new ServiceCollection().AddSingleton<IDeviceConnectionPool>(Pool).BuildServiceProvider();
            Scheduler = new(new UnusedFactory(), _services, NullLogger<CollectionSchedulerService>.Instance);
        }
        public void Dispose() { Scheduler.Dispose(); _services.Dispose(); }
    }

    private sealed class UnusedFactory : IProtocolDriverFactory
    {
        public IProtocolDriver Create(ProtocolType protocol, string brand, string? model = null) =>
            throw new InvalidOperationException("Collection must obtain drivers from the connection pool");
    }

    private sealed class MemoryPool(MemoryDriver driver) : IDeviceConnectionPool
    {
        private int _releaseCount;
        private int _getCount;
        public int FailGetCall { get; set; }
        public bool BlockRelease { get; set; }
        public TaskCompletionSource ReleaseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReleaseCount => Volatile.Read(ref _releaseCount);
        public bool ReleasedDuringRead { get; private set; }
        public int ActiveConnections => 1;
        public Task<IProtocolDriver> GetOrCreateAsync(string deviceId, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _getCount) == FailGetCall)
                throw new InvalidOperationException("Fixture connection initialization failed");
            return Task.FromResult<IProtocolDriver>(driver);
        }
        public bool TryGetConnected(string deviceId, out IProtocolDriver? connected)
        { connected = driver; return true; }
        public async Task ReleaseAsync(string deviceId, CancellationToken ct = default)
        {
            ReleasedDuringRead |= driver.IsReading;
            Interlocked.Increment(ref _releaseCount);
            ReleaseEntered.TrySetResult();
            if (BlockRelease) await AllowRelease.Task;
        }
    }

    private sealed class MemoryDriver : IProtocolDriver
    {
        private int _readCount;
        public int ReadCount => Volatile.Read(ref _readCount);
        public bool BlockRead { get; set; }
        public bool IsReading { get; private set; }
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowReadExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProtocolType Protocol => ProtocolType.ModbusTCP;
        public ConnectionState State => ConnectionState.Connected;
        public DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.BatchRead;
        public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _readCount);
            IsReading = true;
            try
            {
                if (BlockRead)
                {
                    using var registration = ct.Register(() => CancellationObserved.TrySetResult());
                    ReadEntered.TrySetResult();
                    await AllowReadExit.Task;
                    ct.ThrowIfCancellationRequested();
                }
                return requests.Select(tag => new TagValue
                {
                    Address = tag.Address, DataType = tag.DataType,
                    Value = tag.Address == "HR0" ? (object)123 : true,
                    Quality = TagQuality.Good, Timestamp = DateTimeOffset.UtcNow,
                }).ToArray();
            }
            finally { IsReading = false; }
        }
        public Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionResult { Success = true });
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static DeviceCollectionProfile Profile(params CollectionGroupConfig[] groups) =>
        new() { DeviceId = "plc-fixture", Groups = groups };
    private static CollectionGroupConfig Group(string name, int intervalMs, string address) => new()
    {
        GroupName = name, Interval = TimeSpan.FromMilliseconds(intervalMs),
        Tags = [new() { Address = address, DataType = address.StartsWith('C') ? DataType.Bool : DataType.Int16 }],
    };
}
