namespace IndustrialIoT.Host.Services;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Abstractions;
using Microsoft.AspNetCore.Http;

public enum ProgramFileNodeType { Directory, File }
public enum BatchTransferTaskStatus { Pending, Running, Completed, PartialSuccess, Failed }
public enum BatchTransferItemStatus { Pending, Running, Completed, Failed }

public record ProgramFileNodeDto
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required ProgramFileNodeType NodeType { get; init; }
    public long? SizeBytes { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public bool CanDownload { get; init; }
    public bool CanUpload { get; init; }
    public bool HasChildren { get; init; }
}

public record BatchTransferAcceptedResponse
{
    public required string TaskId { get; init; }
    public BatchTransferTaskStatus Status { get; init; }
}

public record BatchTransferItemDto
{
    public required string FileName { get; init; }
    public required string RemotePath { get; init; }
    public BatchTransferItemStatus Status { get; init; }
    public long FileSize { get; init; }
    public long BytesTransferred { get; init; }
    public double? DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
}

public record BatchTransferTaskDto
{
    public required string TaskId { get; init; }
    public required string DeviceId { get; init; }
    public TransferDirection Direction { get; init; }
    public BatchTransferTaskStatus Status { get; init; }
    public int TotalFiles { get; init; }
    public int CompletedFiles { get; init; }
    public int FailedFiles { get; init; }
    public long TotalBytes { get; init; }
    public long BytesTransferred { get; init; }
    public IReadOnlyList<BatchTransferItemDto> Items { get; init; } = [];
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public double? DurationMs { get; init; }
    public double? ThroughputBytesPerSecond { get; init; }
    public bool ArtifactReady { get; init; }
    public string? ArtifactFileName { get; init; }
}

public record BatchDownloadRequest
{
    public required IReadOnlyList<string> Paths { get; init; }
}

public record BatchTransferArtifactResult(string FilePath, string FileName, string ContentType);

public interface IProgramTransferFileBrowserService
{
    Task<IReadOnlyList<ProgramFileNodeDto>> BrowseAsync(
        IAddressSpaceBrowser browser,
        string? path,
        bool recursive = false,
        CancellationToken ct = default);
}

public interface IBatchProgramTransferTaskService
{
    Task<BatchTransferAcceptedResponse> QueueDownloadAsync(string deviceId, IReadOnlyList<string> remotePaths, CancellationToken ct = default);
    Task<BatchTransferAcceptedResponse> QueueUploadAsync(string deviceId, string remotePath, IReadOnlyList<IFormFile> files, CancellationToken ct = default);
    Task<BatchTransferTaskDto?> GetTaskAsync(string taskId, CancellationToken ct = default);
    Task<BatchTransferArtifactResult?> GetArtifactAsync(string taskId, CancellationToken ct = default);
}
