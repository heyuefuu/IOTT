namespace IndustrialIoT.Host.Hubs;

using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Host.Services;
using Microsoft.AspNetCore.SignalR;

/// <summary>
/// SignalR Hub 用于向前端推送设备实时数据
/// 客户端连接: /hubs/device-data
/// </summary>
public class DeviceDataHub : Hub
{
    private readonly ILogger<DeviceDataHub> _logger;
    private readonly DeviceDataSubscriptions _subscriptions;

    public DeviceDataHub(ILogger<DeviceDataHub> logger, DeviceDataSubscriptions subscriptions)
    { _logger = logger; _subscriptions = subscriptions; }

    /// <summary>客户端订阅指定设备的数据推送</summary>
    public async Task SubscribeDevice(string deviceId)
    {
        if (!await _subscriptions.SubscribeAsync(Context.ConnectionId, deviceId, Context.ConnectionAborted))
            throw new HubException("当前账号无权订阅该设备，或登录已过期。");
        _logger.LogInformation("Client {ConnectionId} subscribed to device {DeviceId}",
            Context.ConnectionId, deviceId);
    }

    /// <summary>客户端取消订阅</summary>
    public async Task UnsubscribeDevice(string deviceId)
    {
        _subscriptions.Unsubscribe(Context.ConnectionId, deviceId);
        await Task.CompletedTask;
        _logger.LogInformation("Client {ConnectionId} unsubscribed from device {DeviceId}",
            Context.ConnectionId, deviceId);
    }

    public override Task OnConnectedAsync()
    {
        var token = Context.GetHttpContext()?.Items["IndustrialIoT.Token"] as string;
        if (string.IsNullOrWhiteSpace(token)) throw new HubException("实时订阅需要有效用户会话。");
        _subscriptions.Connect(Context.ConnectionId, token);
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _subscriptions.Disconnect(Context.ConnectionId);
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
