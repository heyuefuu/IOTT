namespace IndustrialIoT.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;

public class EfDeviceRepository : IDeviceRepository
{
    private readonly IoTDbContext _db;
    public EfDeviceRepository(IoTDbContext db) => _db = db;

    public async Task<Device?> GetByIdAsync(string deviceId, CancellationToken ct = default) =>
        await _db.Devices.Include(d => d.CollectionProfiles).FirstOrDefaultAsync(d => d.Id == deviceId, ct);

    public async Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default) =>
        await _db.Devices.Include(d => d.CollectionProfiles).ToListAsync(ct);

    public async Task<IReadOnlyList<Device>> GetByTypeAsync(DeviceType type, CancellationToken ct = default) =>
        await _db.Devices.Where(d => d.Type == type).ToListAsync(ct);

    public async Task AddAsync(Device device, CancellationToken ct = default)
    {
        await _db.Devices.AddAsync(device, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Device device, CancellationToken ct = default)
    {
        _db.Devices.Update(device);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string deviceId, CancellationToken ct = default)
    {
        var device = await _db.Devices.FindAsync([deviceId], ct);
        if (device != null)
        {
            _db.Devices.Remove(device);
            await _db.SaveChangesAsync(ct);
        }
    }
}
