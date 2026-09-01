namespace IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;

public class CollectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string DeviceId { get; set; }
    public required string Name { get; set; }
    public bool IsEnabled { get; set; } = true;
    public ICollection<CollectionGroup> Groups { get; set; } = [];
}

public class CollectionGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string ProfileId { get; set; }
    public required string GroupName { get; set; }
    public required int IntervalMs { get; set; }
    public ICollection<TagConfig> Tags { get; set; } = [];
}

public class TagConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string GroupId { get; set; }
    public required string Address { get; set; }
    public required DataType DataType { get; set; }
    public string? DisplayName { get; set; }
    public string? Unit { get; set; }
    public double? ScaleFactor { get; set; }
    public double? Offset { get; set; }
}
