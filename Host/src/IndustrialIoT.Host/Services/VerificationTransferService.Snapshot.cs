namespace IndustrialIoT.Host.Services;

using System.Security.Cryptography;
using System.Text.Json;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;

public sealed partial class VerificationTransferService
{
    // A process-local MAC avoids exposing a password-derived plain hash. Restart invalidates pending
    // snapshots (fail closed). No credentials or secret-containing configuration are returned/logged.
    private static readonly byte[] SnapshotKey = RandomNumberGenerator.GetBytes(32);

    public async Task<VerificationTransferSnapshot?> GetSnapshotAsync(string deviceId, CancellationToken ct)
    {
        var captured = await CaptureAsync(deviceId, ct);
        if (captured is null) return null;
        var snapshot = ToSnapshot(captured);
        if (!SupportsSafeProtocol(captured.Protocol)) return snapshot;
        await using var driver = factory.Create(captured.Protocol, captured.Brand, captured.Model);
        snapshot.Supported = driver is IVerificationFileTransfer;
        return snapshot;
    }

    private static bool SupportsSafeProtocol(ProtocolType protocol) => protocol == ProtocolType.FTP;

    private async Task<Captured?> CaptureAsync(string deviceId, CancellationToken ct)
    {
        var current = await devices.GetByIdAsync(deviceId, ct);
        if (current is null) return null;
        var primary = CopyDevice(current);
        var target = primary;
        if (primary.ConnectionConfig.Transfer is null
            && primary.ConnectionConfig.ExtendedProperties.TryGetValue("transferDeviceId", out var linkedId)
            && !string.IsNullOrWhiteSpace(linkedId) && !string.Equals(linkedId, primary.Id, StringComparison.OrdinalIgnoreCase))
        {
            var linked = await devices.GetByIdAsync(linkedId, ct);
            if (linked is null) return null;
            target = CopyDevice(linked);
        }
        var transfer = primary.ConnectionConfig.Transfer;
        var config = transfer is null ? target.ConnectionConfig : new DeviceConnectionConfig
        {
            Host = transfer.Host, Port = transfer.Port, Username = transfer.Username, Password = transfer.Password,
            ConnectTimeout = transfer.ConnectTimeout, ReadTimeout = transfer.ReadTimeout,
            ExtendedProperties = new(transfer.ExtendedProperties, StringComparer.Ordinal),
        };
        return new(primary, target, transfer?.Protocol ?? target.Protocol, target.Brand, target.Model, config);
    }

    private static Device CopyDevice(Device device) => new()
    {
        Id = device.Id, Name = device.Name, Type = device.Type, Brand = device.Brand, Model = device.Model, Protocol = device.Protocol,
        ConnectionConfig = device.ConnectionConfig with
        {
            ExtendedProperties = new(device.ConnectionConfig.ExtendedProperties, StringComparer.Ordinal),
            Transfer = device.ConnectionConfig.Transfer is { } transfer
                ? transfer with { ExtendedProperties = new(transfer.ExtendedProperties, StringComparer.Ordinal) } : null,
        },
    };

    private static VerificationTransferSnapshot ToSnapshot(Captured captured)
    {
        var primary = captured.Primary;
        var config = primary.ConnectionConfig;
        // Sorted options make tokens stable across repository reads/reordering. Credentials, protocol,
        // factory selectors, linked-device identity, and all connection options participate in the MAC.
        var raw = JsonSerializer.SerializeToUtf8Bytes(new
        {
            primary.Id, primary.Protocol, primary.Brand, primary.Model,
            PrimaryConfig = TokenConfig(config),
            Transfer = config.Transfer is { } transfer ? new
            {
                transfer.Protocol, transfer.Host, transfer.Port, transfer.Username, transfer.Password,
                transfer.ConnectTimeout, transfer.ReadTimeout,
                Options = new SortedDictionary<string, string>(transfer.ExtendedProperties, StringComparer.Ordinal),
            } : null,
            TargetId = captured.Target.Id, EffectiveProtocol = captured.Protocol, EffectiveBrand = captured.Brand, EffectiveModel = captured.Model,
            EffectiveConfig = TokenConfig(captured.Config),
        });
        var token = Convert.ToHexString(HMACSHA256.HashData(SnapshotKey, raw));
        CryptographicOperations.ZeroMemory(raw);
        return new()
        {
            DeviceId = primary.Id, Token = token, Protocol = captured.Protocol.ToString(),
            Host = captured.Config.Host, Port = captured.Config.Port,
            Primary = new()
            {
                Protocol = primary.Protocol.ToString(), Host = config.Host, Port = config.Port,
                Brand = primary.Brand, Model = primary.Model, Username = config.Username,
                ConnectTimeoutMs = (int)config.ConnectTimeout.TotalMilliseconds,
                ReadTimeoutMs = (int)config.ReadTimeout.TotalMilliseconds,
                // Values here are already exposed by the normal device API; do not include credentials.
                ExtendedProperties = new(config.ExtendedProperties, StringComparer.Ordinal),
                Transfer = config.Transfer is { } t ? new()
                {
                    Protocol = t.Protocol.ToString(), Host = t.Host, Port = t.Port, Username = t.Username,
                    ConnectTimeoutMs = (int)t.ConnectTimeout.TotalMilliseconds, ReadTimeoutMs = (int)t.ReadTimeout.TotalMilliseconds,
                    ExtendedProperties = new(t.ExtendedProperties, StringComparer.Ordinal),
                } : null,
            },
        };
    }

    private static object TokenConfig(DeviceConnectionConfig config) => new
    {
        config.Host, config.Port, config.Username, config.Password, config.ConnectTimeout, config.ReadTimeout,
        Options = new SortedDictionary<string, string>(config.ExtendedProperties, StringComparer.Ordinal),
    };

    private sealed record Captured(Device Primary, Device Target, ProtocolType Protocol, string Brand, string Model, DeviceConnectionConfig Config);
}

public sealed class VerificationTransferSnapshot
{
    public string DeviceId { get; init; } = "";
    public string Token { get; init; } = "";
    public bool Supported { get; set; }
    public string Protocol { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public VerificationPrimaryConfig Primary { get; init; } = new();
}

public sealed class VerificationPrimaryConfig
{
    public string Protocol { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Brand { get; init; } = "";
    public string Model { get; init; } = "";
    public string? Username { get; init; }
    public int ConnectTimeoutMs { get; init; }
    public int ReadTimeoutMs { get; init; }
    public Dictionary<string, string> ExtendedProperties { get; init; } = new(StringComparer.Ordinal);
    public VerificationTransferEndpoint? Transfer { get; init; }
}

public sealed class VerificationTransferEndpoint
{
    public string Protocol { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string? Username { get; init; }
    public int ConnectTimeoutMs { get; init; }
    public int ReadTimeoutMs { get; init; }
    public Dictionary<string, string> ExtendedProperties { get; init; } = new(StringComparer.Ordinal);
}
