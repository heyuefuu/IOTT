namespace IndustrialIoT.Protocols.Models;

public record NCProgramMetadata
{
    public required string FileName { get; init; }
    public required string RemotePath { get; init; }
    public long? FileSize { get; init; }
    public string? Description { get; init; }
}

public record TransferProgress
{
    public required long BytesTransferred { get; init; }
    public required long TotalBytes { get; init; }
    public double Percentage => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes * 100 : 0;
}

public record TransferProgressResult
{
    public required bool Success { get; init; }
    public required string TransferId { get; init; }
    public long BytesTransferred { get; init; }
    public TimeSpan Duration { get; init; }
    public string? Checksum { get; init; }
    public string? ErrorMessage { get; init; }
}
