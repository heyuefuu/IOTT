namespace IndustrialIoT.Host.Hubs;

using IndustrialIoT.Protocols.Models;
using Microsoft.AspNetCore.SignalR;

/// <summary>
/// SignalR Hub 用于向前端推送设备实时数据
/// 客户端连接: /hubs/device-data
/// </summary>
public class DeviceDataHub : Hub
{
    private readonly ILogger<DeviceDataHub> _logger;

    public DeviceDataHub(ILogger<DeviceDataHub> logger) => _logger = logger;

    /// <summary>客户端订阅指定设备的数据推送</summary>
    public async Task SubscribeDevice(string deviceId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"device:{deviceId}");
        _logger.LogInformation("Client {ConnectionId} subscribed to device {DeviceId}",
            Context.ConnectionId, deviceId);
    }

    /// <summary>客户端取消订阅</summary>
    public async Task UnsubscribeDevice(string deviceId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"device:{deviceId}");
        _logger.LogInformation("Client {ConnectionId} unsubscribed from device {DeviceId}",
            Context.ConnectionId, deviceId);
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
