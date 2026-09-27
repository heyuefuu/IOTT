namespace IndustrialIoT.Infrastructure.Persistence;

using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.ValueObjects;

public sealed class EfCollectionImportRepository(IoTDbContext db) : ICollectionImportRepository
{
    public async Task SaveAsync(Device device, DeviceConnectionConfig connectionConfig,
        CollectionProfile profile, CancellationToken ct = default)
    {
        var originalConfig = device.ConnectionConfig;
        var entry = db.Entry(device);
        device.ConnectionConfig = connectionConfig;
        entry.Property(item => item.ConnectionConfig).IsModified = true;
        db.CollectionProfiles.Add(profile);
        try
        {
            // One SaveChanges transaction includes both the map and the complete profile graph.
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            db.ChangeTracker.Clear();
            device.ConnectionConfig = originalConfig;
            device.CollectionProfiles.Remove(profile);
            throw;
        }
    }
}
