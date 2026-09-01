namespace IndustrialIoT.Application.DTOs;

using IndustrialIoT.Domain.Enums;

public record DeviceDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DeviceType Type { get; init; }
    public required string Brand { get; init; }
    public required string Model { get; init; }
    public required ProtocolType Protocol { get; init; }
    public required DeviceStatus Status { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string? Username { get; init; }
    public int ConnectTimeoutMs { get; init; }
    public int ReadTimeoutMs { get; init; }
    public IReadOnlyDictionary<string, string> ExtendedProperties { get; init; } = new Dictionary<string, string>();
    public TransferDeviceDto? Transfer { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
}

public record CreateDeviceRequest
{
    /// <summary>可选：调用方指定设备 ID（网关同步场景需与网关侧保持一致）；留空则自动生成。</summary>
    public string? Id { get; init; }
    public required string Name { get; init; }
    public required DeviceType Type { get; init; }
    public required string Brand { get; init; }
    public required string Model { get; init; }
    public required ProtocolType Protocol { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public int? ConnectTimeoutMs { get; init; }
    public int? ReadTimeoutMs { get; init; }
    public Dictionary<string, string>? ExtendedProperties { get; init; }
    public TransferDeviceRequest? Transfer { get; init; }
}

public record UpdateDeviceRequest
{
    public string? Name { get; init; }
    public DeviceType? Type { get; init; }
    public string? Brand { get; init; }
    public string? Model { get; init; }
    public ProtocolType? Protocol { get; init; }
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public int? ConnectTimeoutMs { get; init; }
    public int? ReadTimeoutMs { get; init; }
    public Dictionary<string, string>? ExtendedProperties { get; init; }
    public TransferDeviceRequest? Transfer { get; init; }
}

public record TransferDeviceDto
{
    public required ProtocolType Protocol { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string? Username { get; init; }
    public int ConnectTimeoutMs { get; init; }
    public int ReadTimeoutMs { get; init; }
    public IReadOnlyDictionary<string, string> ExtendedProperties { get; init; } = new Dictionary<string, string>();
}

public record TransferDeviceRequest
{
    public required ProtocolType Protocol { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public int? ConnectTimeoutMs { get; init; }
    public int? ReadTimeoutMs { get; init; }
    public Dictionary<string, string>? ExtendedProperties { get; init; }
}

public record ConnectionTestResult
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan? Latency { get; init; }
}
