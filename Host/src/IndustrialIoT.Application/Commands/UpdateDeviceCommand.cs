namespace IndustrialIoT.Application.Commands;

using IndustrialIoT.Application.DTOs;
using IndustrialIoT.Domain.Exceptions;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.ValueObjects;
using MediatR;

public record UpdateDeviceCommand(string DeviceId, UpdateDeviceRequest Request) : IRequest<DeviceDto>;

public class UpdateDeviceCommandHandler : IRequestHandler<UpdateDeviceCommand, DeviceDto>
{
    private readonly IDeviceRepository _repo;
    public UpdateDeviceCommandHandler(IDeviceRepository repo) => _repo = repo;

    public async Task<DeviceDto> Handle(UpdateDeviceCommand command, CancellationToken ct)
    {
        var device = await _repo.GetByIdAsync(command.DeviceId, ct)
            ?? throw new DeviceNotFoundException(command.DeviceId);

        var req = command.Request;
        if (req.Name != null) device.Name = req.Name;
        if (req.Type.HasValue) device.Type = req.Type.Value;
        if (req.Brand != null) device.Brand = req.Brand;
        if (req.Model != null) device.Model = req.Model;
        if (req.Protocol.HasValue) device.Protocol = req.Protocol.Value;

        var config = device.ConnectionConfig;
        device.ConnectionConfig = config with
        {
            Host = req.Host ?? config.Host,
            Port = req.Port ?? config.Port,
            Username = req.Username ?? config.Username,
            Password = req.Password ?? config.Password,
            ConnectTimeout = req.ConnectTimeoutMs.HasValue ? TimeSpan.FromMilliseconds(req.ConnectTimeoutMs.Value) : config.ConnectTimeout,
            ReadTimeout = req.ReadTimeoutMs.HasValue ? TimeSpan.FromMilliseconds(req.ReadTimeoutMs.Value) : config.ReadTimeout,
            ExtendedProperties = req.ExtendedProperties ?? config.ExtendedProperties,
            Transfer = req.Transfer is null ? config.Transfer : CreateDeviceCommandHandler.MapTransferRequest(req.Transfer),
        };

        await _repo.UpdateAsync(device, ct);
        return CreateDeviceCommandHandler.MapToDto(device);
    }
}
