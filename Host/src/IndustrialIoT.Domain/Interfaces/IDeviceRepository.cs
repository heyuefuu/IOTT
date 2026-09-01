namespace IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;

public interface IDeviceRepository
{
    Task<Device?> GetByIdAsync(string deviceId, CancellationToken ct = default);
    Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Device>> GetByTypeAsync(DeviceType type, CancellationToken ct = default);
    Task AddAsync(Device device, CancellationToken ct = default);
    Task UpdateAsync(Device device, CancellationToken ct = default);
    Task DeleteAsync(string deviceId, CancellationToken ct = default);
}
