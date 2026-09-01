namespace IndustrialIoT.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Interfaces;

public class EfCollectionProfileRepository : ICollectionProfileRepository
{
    private readonly IoTDbContext _db;
    public EfCollectionProfileRepository(IoTDbContext db) => _db = db;

    public async Task<CollectionProfile?> GetByIdAsync(string id, CancellationToken ct = default) =>
        await _db.CollectionProfiles
            .Include(p => p.Groups)
                .ThenInclude(g => g.Tags)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<CollectionProfile>> GetByDeviceIdAsync(string deviceId, CancellationToken ct = default) =>
        await _db.CollectionProfiles
            .Include(p => p.Groups)
                .ThenInclude(g => g.Tags)
            .Where(p => p.DeviceId == deviceId)
            .ToListAsync(ct);

    public async Task AddAsync(CollectionProfile profile, CancellationToken ct = default)
    {
        await _db.CollectionProfiles.AddAsync(profile, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(CollectionProfile profile, CancellationToken ct = default)
    {
        _db.CollectionProfiles.Update(profile);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var profile = await _db.CollectionProfiles.FindAsync([id], ct);
        if (profile != null)
        {
            _db.CollectionProfiles.Remove(profile);
            await _db.SaveChangesAsync(ct);
        }
    }
}
