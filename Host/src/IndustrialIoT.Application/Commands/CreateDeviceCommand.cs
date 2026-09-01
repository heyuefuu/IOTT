namespace IndustrialIoT.Application.Commands;

using IndustrialIoT.Application.DTOs;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.ValueObjects;
using MediatR;

public record CreateDeviceCommand(CreateDeviceRequest Request) : IRequest<DeviceDto>;

public class CreateDeviceCommandHandler : IRequestHandler<CreateDeviceCommand, DeviceDto>
{
    private readonly IDeviceRepository _repo;
    public CreateDeviceCommandHandler(IDeviceRepository repo) => _repo = repo;

    public async Task<DeviceDto> Handle(CreateDeviceCommand command, CancellationToken ct)
    {
        var req = command.Request;
        if (!string.IsNullOrWhiteSpace(req.Id) && await _repo.GetByIdAsync(req.Id.Trim(), ct) is not null)
            throw new InvalidOperationException($"Device id '{req.Id.Trim()}' already exists");
        var device = new Device
        {
            Name = req.Name,
            Type = req.Type,
            Brand = req.Brand,
            Model = req.Model,
            Protocol = req.Protocol,
            ConnectionConfig = new DeviceConnectionConfig
            {
                Host = req.Host,
                Port = req.Port,
                Username = req.Username,
                Password = req.Password,
                ConnectTimeout = TimeSpan.FromMilliseconds(req.ConnectTimeoutMs ?? 10_000),
                ReadTimeout = TimeSpan.FromMilliseconds(req.ReadTimeoutMs ?? 5_000),
                ExtendedProperties = req.ExtendedProperties ?? [],
                Transfer = MapTransferRequest(req.Transfer),
            },
        };
        if (!string.IsNullOrWhiteSpace(req.Id))
            device.Id = req.Id.Trim();
        await _repo.AddAsync(device, ct);
        return MapToDto(device);
    }

    internal static DeviceDto MapToDto(Device d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Type = d.Type,
        Brand = d.Brand,
        Model = d.Model,
        Protocol = d.Protocol,
        Status = d.Status,
        Host = d.ConnectionConfig.Host,
        Port = d.ConnectionConfig.Port,
        Username = d.ConnectionConfig.Username,
        ConnectTimeoutMs = (int)d.ConnectionConfig.ConnectTimeout.TotalMilliseconds,
        ReadTimeoutMs = (int)d.ConnectionConfig.ReadTimeout.TotalMilliseconds,
        ExtendedProperties = new Dictionary<string, string>(d.ConnectionConfig.ExtendedProperties),
        Transfer = MapTransferDto(d.ConnectionConfig.Transfer),
        CreatedAt = d.CreatedAt,
        LastSeenAt = d.LastSeenAt,
    };

    internal static TransferConnectionConfig? MapTransferRequest(TransferDeviceRequest? transfer) =>
        transfer is null ? null : new TransferConnectionConfig
        {
            Protocol = transfer.Protocol,
            Host = transfer.Host,
            Port = transfer.Port,
            Username = transfer.Username,
            Password = transfer.Password,
            ConnectTimeout = TimeSpan.FromMilliseconds(transfer.ConnectTimeoutMs ?? 10_000),
            ReadTimeout = TimeSpan.FromMilliseconds(transfer.ReadTimeoutMs ?? 5_000),
            ExtendedProperties = transfer.ExtendedProperties ?? [],
        };

    private static TransferDeviceDto? MapTransferDto(TransferConnectionConfig? transfer) =>
        transfer is null ? null : new TransferDeviceDto
        {
            Protocol = transfer.Protocol,
            Host = transfer.Host,
            Port = transfer.Port,
            Username = transfer.Username,
            ConnectTimeoutMs = (int)transfer.ConnectTimeout.TotalMilliseconds,
            ReadTimeoutMs = (int)transfer.ReadTimeout.TotalMilliseconds,
            ExtendedProperties = new Dictionary<string, string>(transfer.ExtendedProperties),
        };
}
