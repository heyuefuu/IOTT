namespace IndustrialIoT.Protocols.Models;

using IndustrialIoT.Domain.Enums;

public record TagValue
{
    public required string Address { get; init; }
    public required DataType DataType { get; init; }
    public required object Value { get; init; }
    public required TagQuality Quality { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? ErrorMessage { get; init; }
}

public record TagReadRequest
{
    public required string Address { get; init; }
    public required DataType DataType { get; init; }
}
