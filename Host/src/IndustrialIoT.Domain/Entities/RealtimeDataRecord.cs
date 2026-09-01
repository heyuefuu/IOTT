namespace IndustrialIoT.Domain.Entities;

public class RealtimeDataRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string DeviceId { get; set; }
    public required string GroupName { get; set; }
    public required string PayloadJson { get; set; }
    public required DateTimeOffset CollectedAt { get; set; }
    public required DateTimeOffset StoredAt { get; set; } = DateTimeOffset.UtcNow;
    public int ValueCount { get; set; }
    public double CollectionDurationMs { get; set; }
}
