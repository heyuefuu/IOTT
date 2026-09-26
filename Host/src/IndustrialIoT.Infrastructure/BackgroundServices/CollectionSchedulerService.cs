namespace IndustrialIoT.Infrastructure.BackgroundServices;

using System.Collections.Concurrent;
using System.Threading.Channels;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.NCLink;
using IndustrialIoT.Protocols.Pipeline;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public class CollectionSchedulerService : BackgroundService, ICollectionPipeline
{
    private readonly ILogger<CollectionSchedulerService> _logger;
    private readonly Channel<CollectedDataBatch> _channel;
    private readonly ConcurrentDictionary<string, CollectionTaskEntry> _tasks = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceLifecycleLocks = new();
    private readonly IProtocolDriverFactory _driverFactory;
    private readonly IServiceProvider _serviceProvider;

    public CollectionSchedulerService(IProtocolDriverFactory driverFactory, IServiceProvider serviceProvider, ILogger<CollectionSchedulerService> logger)
    {
        _driverFactory = driverFactory;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _channel = Channel.CreateBounded<CollectedDataBatch>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public ChannelReader<CollectedDataBatch> GetOutputReader() => _channel.Reader;

    public async Task<string> StartCollectionAsync(DeviceCollectionProfile profile, CancellationToken ct = default)
    {
        var lifecycleGate = _deviceLifecycleLocks.GetOrAdd(profile.DeviceId, _ => new SemaphoreSlim(1, 1));
        await lifecycleGate.WaitAsync(ct);
        var taskId = Guid.NewGuid().ToString("N");
        var cts = new CancellationTokenSource();
        var entry = new CollectionTaskEntry
        {
            TaskId = taskId,
            DeviceId = profile.DeviceId,
            Profile = profile,
            Cts = cts,
            LifecycleGate = lifecycleGate,
            IsRunning = true,
        };
        try { _tasks[taskId] = entry; }
        finally { lifecycleGate.Release(); }
        _ = RunCollectionAsync(entry);
        _logger.LogInformation("Started collection task {TaskId} for device {DeviceId}", taskId, profile.DeviceId);
        return taskId;
    }

    public async Task StopCollectionAsync(string taskId, CancellationToken ct = default)
    {
        if (_tasks.TryGetValue(taskId, out var entry))
        {
            try { entry.Cts.Cancel(); }
            catch (ObjectDisposedException) { }
            await entry.Completion.Task.WaitAsync(ct);
            _logger.LogInformation("Stopped collection task {TaskId} for device {DeviceId}", taskId, entry.DeviceId);
        }
    }

    public IReadOnlyDictionary<string, CollectionTaskStatus> GetActiveTasksSnapshot() =>
        _tasks.ToDictionary(kv => kv.Key, kv => new CollectionTaskStatus
        {
            TaskId = kv.Key,
            DeviceId = kv.Value.DeviceId,
            IsRunning = kv.Value.IsRunning,
            LastCollectedAt = kv.Value.LastCollectedAt,
            TotalCollections = kv.Value.TotalCollections,
            TotalErrors = kv.Value.TotalErrors,
        });

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CollectionSchedulerService started");
        return Task.CompletedTask; // tasks are started on demand
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var entries = _tasks.Values.ToArray();
        foreach (var entry in entries)
        {
            try { entry.Cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        await Task.WhenAll(entries.Select(entry => entry.Completion.Task)).WaitAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private async Task RunCollectionAsync(CollectionTaskEntry entry)
    {
        try
        {
            // For each group, run a periodic collection loop
            var groupTasks = entry.Profile.Groups.Select(async group =>
            {
                try { await RunGroupLoopAsync(entry, group, entry.Cts.Token); }
                catch { entry.Cts.Cancel(); throw; }
            });
            await Task.WhenAll(groupTasks);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Collection task {TaskId} faulted", entry.TaskId);
        }
        finally
        {
            await entry.LifecycleGate.WaitAsync();
            try
            {
                entry.IsRunning = false;

                // All group reads have completed; this is the sole connection-release path.
                var deviceStillInUse = _tasks.Values.Any(t => t.DeviceId == entry.DeviceId && t.IsRunning);
                if (!deviceStillInUse)
                {
                    try
                    {
                        var pool = _serviceProvider.GetRequiredService<IDeviceConnectionPool>();
                        await pool.ReleaseAsync(entry.DeviceId);
                        _logger.LogDebug("Released connection for device {DeviceId} in task finally block", entry.DeviceId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to release connection for device {DeviceId} in finally block", entry.DeviceId);
                    }
                }

                _tasks.TryRemove(entry.TaskId, out _);
            }
            finally { entry.LifecycleGate.Release(); }
            entry.Cts.Dispose();
            entry.Completion.TrySetResult();
        }
    }

    private async Task RunGroupLoopAsync(CollectionTaskEntry entry, CollectionGroupConfig group, CancellationToken ct)
    {
        var pool = _serviceProvider.GetRequiredService<IDeviceConnectionPool>();
        var initialDriver = await pool.GetOrCreateAsync(entry.DeviceId, ct);
        if (TryResolveNCLinkSampleChannel(initialDriver, group, out var sampleChannelId))
        {
            await RunNCLinkSampleLoopAsync(entry, (NCLinkDriver)initialDriver, group, sampleChannelId, ct);
            return;
        }

        using var timer = new PeriodicTimer(group.Interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                // Re-resolve every tick: the pool evicts and disposes a driver whenever a health
                // check or another caller sees it fail, and a disposed FOCAS driver has already run
                // cnc_freelibhndl. Holding the reference captured before the loop meant one eviction
                // broke this task permanently while manual reads (which always get a fresh pooled
                // driver) kept working — the "collection works, then stops, then works" symptom.
                var driver = await pool.GetOrCreateAsync(entry.DeviceId, ct);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var values = await driver.ReadTagsAsync(group.Tags, ct);
                sw.Stop();

                var batch = new CollectedDataBatch
                {
                    DeviceId = entry.DeviceId,
                    GroupName = group.GroupName,
                    Values = values,
                    CollectedAt = DateTimeOffset.UtcNow,
                    CollectionDuration = sw.Elapsed,
                };

                await _channel.Writer.WriteAsync(batch, ct);
                entry.LastCollectedAt = DateTimeOffset.UtcNow;
                entry.TotalCollections++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                entry.TotalErrors++;
                _logger.LogWarning(ex, "Collection error for device {DeviceId} group {Group}", entry.DeviceId, group.GroupName);
            }
        }
    }

    private bool TryResolveNCLinkSampleChannel(IProtocolDriver driver, CollectionGroupConfig group, out string channelId)
    {
        channelId = string.Empty;
        if (driver is not NCLinkDriver ncLinkDriver || ncLinkDriver.DeviceModel?.SampleChannels.Count is not > 0)
            return false;

        channelId = ncLinkDriver.DeviceModel.SampleChannels
            .Select(channel => channel.Id)
            .FirstOrDefault(id => id.Equals(group.GroupName, StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;
        return !string.IsNullOrEmpty(channelId);
    }

    private async Task RunNCLinkSampleLoopAsync(
        CollectionTaskEntry entry, NCLinkDriver driver, CollectionGroupConfig group, string sampleChannelId, CancellationToken ct)
    {
        var logger = _serviceProvider.GetService<ILogger<NCLinkSampleSubscriber>>()
            ?? NullLogger<NCLinkSampleSubscriber>.Instance;
        await using var subscriber = new NCLinkSampleSubscriber(logger);
        subscriber.Bind(driver, entry.DeviceId);

        await foreach (var batch in subscriber.Output.ReadAllAsync(ct))
        {
            if (!string.Equals(batch.GroupName, sampleChannelId, StringComparison.OrdinalIgnoreCase))
                continue;

            var values = FilterSampleValues(batch.Values, group.Tags);
            if (group.Tags.Count > 0 && values.Count == 0)
                continue;

            await _channel.Writer.WriteAsync(batch with { GroupName = group.GroupName, Values = values }, ct);
            entry.LastCollectedAt = DateTimeOffset.UtcNow;
            entry.TotalCollections++;
        }
    }

    private static IReadOnlyList<TagValue> FilterSampleValues(
        IReadOnlyList<TagValue> values, IReadOnlyList<TagReadRequest> tags)
    {
        if (tags.Count == 0) return values;
        var addresses = tags.Select(tag => tag.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return values.Where(value => addresses.Contains(value.Address)).ToList();
    }

    private class CollectionTaskEntry
    {
        public required string TaskId { get; init; }
        public required string DeviceId { get; init; }
        public required DeviceCollectionProfile Profile { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required SemaphoreSlim LifecycleGate { get; init; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsRunning { get; set; }
        public DateTimeOffset? LastCollectedAt { get; set; }
        public long TotalCollections { get; set; }
        public long TotalErrors { get; set; }
    }
}
