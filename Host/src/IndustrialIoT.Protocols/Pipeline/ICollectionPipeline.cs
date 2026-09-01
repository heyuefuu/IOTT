namespace IndustrialIoT.Protocols.Pipeline;

using IndustrialIoT.Protocols.Models;
using System.Threading.Channels;

public interface ICollectionPipeline
{
    Task<string> StartCollectionAsync(DeviceCollectionProfile profile, CancellationToken ct = default);
    Task StopCollectionAsync(string taskId, CancellationToken ct = default);
    ChannelReader<CollectedDataBatch> GetOutputReader();
    IReadOnlyDictionary<string, CollectionTaskStatus> GetActiveTasksSnapshot();
}

public record DeviceCollectionProfile
{
    public required string DeviceId { get; init; }
    public required IReadOnlyList<CollectionGroupConfig> Groups { get; init; }
}

public record CollectionGroupConfig
{
    public required string GroupName { get; init; }
    public required TimeSpan Interval { get; init; }
    public required IReadOnlyList<TagReadRequest> Tags { get; init; }
    public int MaxRetries { get; init; } = 3;
}

public record CollectionTaskStatus
{
    public required string TaskId { get; init; }
    public required string DeviceId { get; init; }
    public required bool IsRunning { get; init; }
    public DateTimeOffset? LastCollectedAt { get; init; }
    public long TotalCollections { get; init; }
    public long TotalErrors { get; init; }
}
