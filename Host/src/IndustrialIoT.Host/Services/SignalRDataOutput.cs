namespace IndustrialIoT.Host.Services;

using IndustrialIoT.Host.Hubs;
using IndustrialIoT.Infrastructure.Messaging;
using IndustrialIoT.Protocols.Models;
using Microsoft.AspNetCore.SignalR;

/// <summary>
/// IDataOutput implementation that pushes collected data batches
/// to SignalR clients subscribed to the corresponding device group.
/// </summary>
public class SignalRDataOutput : IDataOutput
{
    private readonly IHubContext<DeviceDataHub> _hubContext;
    private readonly ILogger<SignalRDataOutput> _logger;

    public SignalRDataOutput(
        IHubContext<DeviceDataHub> hubContext,
        ILogger<SignalRDataOutput> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public string Name => "SignalR";

    public Task InitializeAsync(CancellationToken ct)
    {
        _logger.LogInformation("SignalR data output initialized");
        return Task.CompletedTask;
    }

    public async Task WriteAsync(CollectedDataBatch batch, CancellationToken ct)
    {
        var groupName = $"device:{batch.DeviceId}";
        await _hubContext.Clients.Group(groupName)
            .SendAsync("DeviceDataReceived", batch, ct);
    }

    public Task FlushAsync(CancellationToken ct)
    {
        // SignalR sends immediately; nothing to flush
        return Task.CompletedTask;
    }
}
