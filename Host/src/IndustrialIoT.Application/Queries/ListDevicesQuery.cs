namespace IndustrialIoT.Application.Queries;

using IndustrialIoT.Application.Commands;
using IndustrialIoT.Application.DTOs;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using MediatR;

public record ListDevicesQuery(DeviceType? Type = null) : IRequest<IReadOnlyList<DeviceDto>>;

public class ListDevicesQueryHandler : IRequestHandler<ListDevicesQuery, IReadOnlyList<DeviceDto>>
{
    private readonly IDeviceRepository _repo;
    public ListDevicesQueryHandler(IDeviceRepository repo) => _repo = repo;

    public async Task<IReadOnlyList<DeviceDto>> Handle(ListDevicesQuery query, CancellationToken ct)
    {
        var devices = query.Type.HasValue
            ? await _repo.GetByTypeAsync(query.Type.Value, ct)
            : await _repo.GetAllAsync(ct);
        return devices.Select(CreateDeviceCommandHandler.MapToDto).ToList();
    }
}
