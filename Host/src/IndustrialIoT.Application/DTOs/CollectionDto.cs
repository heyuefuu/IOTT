namespace IndustrialIoT.Application.DTOs;

using IndustrialIoT.Domain.Enums;

public record CollectionProfileDto
{
    public required string Id { get; init; }
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public bool IsEnabled { get; init; }
    public required IReadOnlyList<CollectionGroupDto> Groups { get; init; }
}

public record CollectionGroupDto
{
    public required string GroupName { get; init; }
    public required int IntervalMs { get; init; }
    public required IReadOnlyList<TagConfigDto> Tags { get; init; }
}

public record TagConfigDto
{
    public required string Address { get; init; }
    public required DataType DataType { get; init; }
    public string? DisplayName { get; init; }
    public string? Unit { get; init; }
}

public record StartCollectionRequest
{
    public required string DeviceId { get; init; }
    public required IReadOnlyList<CollectionGroupDto> Groups { get; init; }
}

public record CollectionStatusDto
{
    public required string TaskId { get; init; }
    public required string DeviceId { get; init; }
    public required bool IsRunning { get; init; }
    public DateTimeOffset? LastCollectedAt { get; init; }
    public long TotalCollections { get; init; }
    public long TotalErrors { get; init; }
}
