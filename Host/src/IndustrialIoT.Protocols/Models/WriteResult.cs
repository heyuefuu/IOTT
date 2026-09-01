namespace IndustrialIoT.Protocols.Models;

public record WriteResult
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public record ConnectionResult
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.UtcNow;
}
