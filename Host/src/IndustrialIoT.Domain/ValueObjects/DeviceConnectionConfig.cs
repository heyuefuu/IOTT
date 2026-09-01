namespace IndustrialIoT.Domain.ValueObjects;
public record DeviceConnectionConfig
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public Dictionary<string, string> ExtendedProperties { get; init; } = [];
    public TransferConnectionConfig? Transfer { get; init; }
}
