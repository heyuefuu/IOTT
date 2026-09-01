namespace IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.Entities;

public interface INCProgramRepository
{
    Task<NCProgram?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<NCProgram>> GetByDeviceIdAsync(string deviceId, CancellationToken ct = default);
    Task AddAsync(NCProgram program, CancellationToken ct = default);
    Task UpdateAsync(NCProgram program, CancellationToken ct = default);
}
