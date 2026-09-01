namespace IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;

public class Device
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string Name { get; set; }
    public required DeviceType Type { get; set; }
    public required string Brand { get; set; }
    public required string Model { get; set; }
    public required ProtocolType Protocol { get; set; }
    public required DeviceConnectionConfig ConnectionConfig { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.Offline;
    public ICollection<CollectionProfile> CollectionProfiles { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];
}
