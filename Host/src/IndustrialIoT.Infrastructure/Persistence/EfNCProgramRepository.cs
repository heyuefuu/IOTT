namespace IndustrialIoT.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Interfaces;

public class EfNCProgramRepository : INCProgramRepository
{
    private readonly IoTDbContext _db;
    public EfNCProgramRepository(IoTDbContext db) => _db = db;

    public async Task<NCProgram?> GetByIdAsync(string id, CancellationToken ct = default) =>
        await _db.NCPrograms.FindAsync([id], ct);

    public async Task<IReadOnlyList<NCProgram>> GetByDeviceIdAsync(string deviceId, CancellationToken ct = default) =>
        await _db.NCPrograms.Where(p => p.DeviceId == deviceId).OrderByDescending(p => p.StartedAt).ToListAsync(ct);

    public async Task AddAsync(NCProgram program, CancellationToken ct = default)
    {
        await _db.NCPrograms.AddAsync(program, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(NCProgram program, CancellationToken ct = default)
    {
        _db.NCPrograms.Update(program);
        await _db.SaveChangesAsync(ct);
    }
}
