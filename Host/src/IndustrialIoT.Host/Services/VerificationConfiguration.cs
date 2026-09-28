namespace IndustrialIoT.Host.Services;

using System.Security.Cryptography;
using System.Text.Json;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;

/// <summary>
/// Verification wire contract v1. The public digest binds all routing/driver-selection fields from
/// DeviceDto; the keyed revision additionally binds secrets and a linked transfer device. Neither
/// raw passwords nor exception messages from drivers are returned or logged by this boundary.
/// These digests are configuration constraints, NOT authorization tokens.
/// </summary>
public sealed record VerificationExpectation
{
    public string ConfigurationFingerprint { get; init; } = "";
    public string? Revision { get; init; }
}

public sealed record VerificationEvidence
{
    public int Version { get; init; } = 1;
    public string Operation { get; init; } = "";
    public string Protocol { get; init; } = "";
    public bool Simulated { get; init; }
    public bool ConfigurationMatched { get; init; }
    public bool OperationAttempted { get; init; }
    public string ConfigurationFingerprint { get; init; } = "";
    public string Revision { get; init; } = "";
    public string Code { get; init; } = "";
}

public sealed record VerificationExecution<T>(int StatusCode, string? Error, VerificationEvidence Evidence, T? Value);

public static class VerificationConfiguration
{
    private static readonly byte[] RevisionKey = RandomNumberGenerator.GetBytes(32);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Array order is part of v1 and is mirrored by the Gateway adapter. Use ordinal key sorting and
    // exact enum names here, not scoring-family aliases (e.g. ModbusTCP is NOT a ModbusRTU route).
    public static string Fingerprint(Device device)
    {
        var config = device.ConnectionConfig;
        var transfer = config.Transfer;
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            "verification-config-v1", device.Id, device.Protocol.ToString(), device.Brand, device.Model,
            config.Host, config.Port, config.Username, (int)config.ConnectTimeout.TotalMilliseconds,
            (int)config.ReadTimeout.TotalMilliseconds, Properties(config.ExtendedProperties),
            transfer is null ? null : new object?[]
            {
                transfer.Protocol.ToString(), transfer.Host, transfer.Port, transfer.Username,
                (int)transfer.ConnectTimeout.TotalMilliseconds, (int)transfer.ReadTimeout.TotalMilliseconds,
                Properties(transfer.ExtendedProperties),
            },
        }, JsonOptions)));
    }

    public static string Revision(Device source, Device target, bool transfer) => Convert.ToHexString(
        HMACSHA256.HashData(RevisionKey, JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            "verification-revision-v1", transfer, Fingerprint(source), source.ConnectionConfig.Password,
            source.ConnectionConfig.Transfer?.Password, Fingerprint(target), target.ConnectionConfig.Password,
            target.ConnectionConfig.Transfer?.Password,
        }, JsonOptions)));

    public static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static bool IsSimulated(IProtocolDriver driver)
    {
        if (driver.Protocol == ProtocolType.Simulator) return true;
        // The registered SimulatorDriver cannot become evidence by being registered under another
        // protocol. Include base types so a subclass cannot hide its simulator identity either.
        for (var type = driver.GetType(); type is not null; type = type.BaseType)
        {
            var name = type.FullName ?? type.Name;
            if (name.Contains("Simulator", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Simulated", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static Device Snapshot(Device device) => new()
    {
        Id = device.Id, Name = device.Name, Type = device.Type, Brand = device.Brand, Model = device.Model,
        Protocol = device.Protocol, ConnectionConfig = Clone(device.ConnectionConfig),
    };

    public static DeviceConnectionConfig Clone(DeviceConnectionConfig config) => config with
    {
        ExtendedProperties = new Dictionary<string, string>(config.ExtendedProperties, StringComparer.Ordinal),
        Transfer = config.Transfer is not { } transfer ? null : transfer with
        {
            ExtendedProperties = new Dictionary<string, string>(transfer.ExtendedProperties, StringComparer.Ordinal),
        },
    };

    public static DeviceConnectionConfig TransferConfig(TransferConnectionConfig transfer) => new()
    {
        Host = transfer.Host, Port = transfer.Port, Username = transfer.Username, Password = transfer.Password,
        ConnectTimeout = transfer.ConnectTimeout, ReadTimeout = transfer.ReadTimeout,
        ExtendedProperties = new Dictionary<string, string>(transfer.ExtendedProperties, StringComparer.Ordinal),
    };

    private static string[][] Properties(Dictionary<string, string> properties) => properties
        .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new[] { pair.Key, pair.Value }).ToArray();
}
