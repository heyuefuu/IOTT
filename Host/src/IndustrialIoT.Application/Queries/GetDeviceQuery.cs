namespace IndustrialIoT.Application.Queries;

using IndustrialIoT.Application.Commands;
using IndustrialIoT.Application.DTOs;
using IndustrialIoT.Domain.Interfaces;
using MediatR;

public record GetDeviceQuery(string DeviceId) : IRequest<DeviceDto?>;

public class GetDeviceQueryHandler : IRequestHandler<GetDeviceQuery, DeviceDto?>
{
    private readonly IDeviceRepository _repo;
    public GetDeviceQueryHandler(IDeviceRepository repo) => _repo = repo;

    public async Task<DeviceDto?> Handle(GetDeviceQuery query, CancellationToken ct)
    {
        var device = await _repo.GetByIdAsync(query.DeviceId, ct);
        return device is null ? null : CreateDeviceCommandHandler.MapToDto(device);
    }
}
