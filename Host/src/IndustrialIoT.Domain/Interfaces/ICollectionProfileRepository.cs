namespace IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.Entities;

public interface ICollectionProfileRepository
{
    Task<CollectionProfile?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<CollectionProfile>> GetByDeviceIdAsync(string deviceId, CancellationToken ct = default);
    Task AddAsync(CollectionProfile profile, CancellationToken ct = default);
    Task UpdateAsync(CollectionProfile profile, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}
