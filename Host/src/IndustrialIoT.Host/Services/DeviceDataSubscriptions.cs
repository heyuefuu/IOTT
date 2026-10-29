namespace IndustrialIoT.Host.Services;

using System.Collections.Concurrent;

public sealed class DeviceDataSubscriptions(HostAccessValidator authorization)
{
    private sealed record Subscription(string Token, ConcurrentDictionary<string, byte> Devices);
    private readonly ConcurrentDictionary<string, Subscription> _connections = new();

    public void Connect(string connectionId, string token) => _connections[connectionId] = new(token, new());
    public void Disconnect(string connectionId) => _connections.TryRemove(connectionId, out _);
    public void Unsubscribe(string connectionId, string deviceId)
    {
        if (_connections.TryGetValue(connectionId, out var subscription)) subscription.Devices.TryRemove(deviceId, out _);
    }

    public async Task<bool> SubscribeAsync(string connectionId, string deviceId, CancellationToken ct)
    {
        if (!_connections.TryGetValue(connectionId, out var subscription)) return false;
        var grant = await authorization.ValidateAsync(subscription.Token, ct);
        if (grant is null || !(grant.HasPermission("data_read") || grant.HasPermission("device_manage"))
            || !grant.Scope.Allows(deviceId)) return false;
        subscription.Devices[deviceId] = 0;
        return true;
    }

    public async Task<IReadOnlyList<string>> RecipientsAsync(string deviceId, CancellationToken ct)
    {
        var recipients = new List<string>();
        foreach (var group in _connections.Where(pair => pair.Value.Devices.ContainsKey(deviceId)).GroupBy(pair => pair.Value.Token))
        {
            // Validate every emitted batch so logout, disabling, or a scope change stops existing subscriptions.
            var grant = await authorization.ValidateAsync(group.Key, ct);
            if (grant is not null && grant.Scope.Allows(deviceId)
                && (grant.HasPermission("data_read") || grant.HasPermission("device_manage")))
                recipients.AddRange(group.Select(pair => pair.Key));
            else foreach (var pair in group) _connections.TryRemove(pair.Key, out _);
        }
        return recipients;
    }
}
