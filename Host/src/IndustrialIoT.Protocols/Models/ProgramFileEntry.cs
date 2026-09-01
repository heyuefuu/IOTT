namespace IndustrialIoT.Protocols.Models;

public record ProgramFileEntry
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public long? SizeBytes { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public bool CanDownload { get; init; }
    public bool CanUpload { get; init; }
    public bool HasChildren { get; init; }
    public string? Comment { get; init; }
}
