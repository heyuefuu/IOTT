namespace IottDevMock.Mock;

// ---------- 程序传输 ----------

public record ProgramFileNodeDto
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string NodeType { get; init; }   // Directory / File
    public long? SizeBytes { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public bool CanDownload { get; init; }
    public bool CanUpload { get; init; }
    public bool HasChildren { get; init; }
}

public record ProgramTransferResponseDto
{
    public required string TransferId { get; init; }
    public required string DeviceId { get; init; }
    public required string FileName { get; init; }
    public required string RemotePath { get; init; }
    public required string Direction { get; init; }   // Upload / Download
    public required string Status { get; init; }      // Pending / InProgress / Paused / Completed / Failed
    public long FileSize { get; init; }
    public long BytesTransferred { get; init; }
    public string? Checksum { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public double? DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
}

public record ProgramTransferCapabilityDto
{
    public required string Protocol { get; init; }
    public required bool SupportsProgramTransfer { get; init; }
    public required bool SupportsBrowse { get; init; }
    public required bool SupportsResumeUpload { get; init; }
    public required bool SupportsFullRestartUpload { get; init; }
    public required string ResumeMode { get; init; }
    public string? Limitation { get; init; }
}

public record DownloadProgramRequest
{
    public required string RemotePath { get; init; }
}

public record ResumeUploadRequest
{
    public required string TransferId { get; init; }
    public long Offset { get; init; }
}

public record BatchDownloadRequest
{
    public required IReadOnlyList<string> Paths { get; init; }
}

public record BatchTransferAcceptedDto
{
    public required string TaskId { get; init; }
    public required string Status { get; init; }   // Pending / Running / Completed / PartialSuccess / Failed
}

public record BatchTransferItemDto
{
    public required string FileName { get; init; }
    public required string RemotePath { get; init; }
    public required string Status { get; init; }   // Pending / Running / Completed / Failed
    public long FileSize { get; init; }
    public long BytesTransferred { get; init; }
    public double? DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
}

public record BatchTransferTaskDto
{
    public required string TaskId { get; init; }
    public required string DeviceId { get; init; }
    public required string Direction { get; init; }
    public required string Status { get; init; }
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

// ---------- 采集 ----------

public record TagConfigDto
{
    public required string Address { get; init; }
    public required string DataType { get; init; }
    public string? DisplayName { get; init; }
    public string? Unit { get; init; }
}

public record CollectionGroupDto
{
    public required string GroupName { get; init; }
    public required int IntervalMs { get; init; }
    public required IReadOnlyList<TagConfigDto> Tags { get; init; }
}

public record CollectionProfileDto
{
    public required string Id { get; init; }
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public bool IsEnabled { get; init; } = true;
    public required IReadOnlyList<CollectionGroupDto> Groups { get; init; }
}

public record StartCollectionRequest
{
    public required string DeviceId { get; init; }
    public required IReadOnlyList<CollectionGroupDto> Groups { get; init; }
}

public record CollectionStatusDto
{
    public required string TaskId { get; init; }
    public required string DeviceId { get; init; }
    public required bool IsRunning { get; init; }
    public DateTimeOffset? LastCollectedAt { get; init; }
    public long TotalCollections { get; init; }
    public long TotalErrors { get; init; }
}

public record CollectionProfileCreateRequest
{
    public required string Name { get; init; }
    public required IReadOnlyList<CollectionGroupDto> Groups { get; init; }
}

// ---------- 批量导入 ----------

public record BatchImportResultDto
{
    public string? AddressMap { get; init; }
    public int TotalRows { get; init; }
    public int SuccessCount { get; init; }
    public int ErrorCount { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
}
