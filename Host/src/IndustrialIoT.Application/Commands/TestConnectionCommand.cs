namespace IndustrialIoT.Application.Commands;

using IndustrialIoT.Application.DTOs;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Exceptions;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Protocols.Registration;
using MediatR;

public record TestConnectionCommand(string DeviceId) : IRequest<ConnectionTestResult>;

public class TestConnectionCommandHandler : IRequestHandler<TestConnectionCommand, ConnectionTestResult>
{
    private readonly IDeviceRepository _repo;
    private readonly IProtocolDriverFactory _driverFactory;

    public TestConnectionCommandHandler(IDeviceRepository repo, IProtocolDriverFactory driverFactory)
    {
        _repo = repo;
        _driverFactory = driverFactory;
    }

    public async Task<ConnectionTestResult> Handle(TestConnectionCommand command, CancellationToken ct)
    {
        var device = await _repo.GetByIdAsync(command.DeviceId, ct)
            ?? throw new DeviceNotFoundException(command.DeviceId);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var driver = _driverFactory.Create(device.Protocol, device.Brand, device.Model);
        var result = await driver.ConnectAsync(device.ConnectionConfig, ct);
        sw.Stop();

        if (result.Success)
            await driver.DisconnectAsync(ct);

        device.Status = result.Success ? DeviceStatus.Online : DeviceStatus.Error;
        device.LastSeenAt = DateTimeOffset.UtcNow;
        await _repo.UpdateAsync(device, ct);

        return new ConnectionTestResult
        {
            Success = result.Success,
            ErrorMessage = result.ErrorMessage,
            Latency = sw.Elapsed,
        };
    }
}
