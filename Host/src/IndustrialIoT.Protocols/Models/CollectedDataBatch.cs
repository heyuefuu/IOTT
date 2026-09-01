namespace IndustrialIoT.Protocols.Models;

public record CollectedDataBatch
{
    public required string DeviceId { get; init; }
    public required string GroupName { get; init; }
    public required IReadOnlyList<TagValue> Values { get; init; }
    public required DateTimeOffset CollectedAt { get; init; }
    public TimeSpan CollectionDuration { get; init; }
}
