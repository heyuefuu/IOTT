namespace IndustrialIoT.Domain.Interfaces;

using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.ValueObjects;

public interface ICollectionImportRepository
{
    Task SaveAsync(Device device, DeviceConnectionConfig connectionConfig,
        CollectionProfile profile, CancellationToken ct = default);
}
