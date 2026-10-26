namespace IottDevMock.Mock;

/// <summary>内存设备（Mock 的唯一数据源，serve 与 seed 共用）。</summary>
public sealed class MockDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }        // CNC / PLC / Robot
    public required string Brand { get; init; }
    public required string Model { get; init; }
    public required string Protocol { get; init; }    // ProtocolType 名，如 ModbusTCP / OpcUa
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string? Username { get; init; }
    public int ConnectTimeoutMs { get; init; } = 10000;
    public int ReadTimeoutMs { get; init; } = 5000;
    public Dictionary<string, string> ExtendedProperties { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public MockTransfer? Transfer { get; init; }
    public bool Browsable { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class MockTransfer
{
    public required string Protocol { get; init; }   // FTP / SMB / NFS / GskrmFileTransfer / Serial
    public required string Host { get; init; }
    public required int Port { get; init; }
    public Dictionary<string, string> ExtendedProperties { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

// ---------- Host 契约 DTO（字段与 Host 对齐，枚举以字符串输出） ----------

public record DeviceDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string Brand { get; init; }
    public required string Model { get; init; }
    public required string Protocol { get; init; }
    public required string Status { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string? Username { get; init; }
    public int ConnectTimeoutMs { get; init; }
    public int ReadTimeoutMs { get; init; }
    public IReadOnlyDictionary<string, string> ExtendedProperties { get; init; } = new Dictionary<string, string>();
    public TransferDto? Transfer { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
}

public record TransferDto
{
    public required string Protocol { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string? Username { get; init; }
    public int ConnectTimeoutMs { get; init; }
    public int ReadTimeoutMs { get; init; }
    public IReadOnlyDictionary<string, string> ExtendedProperties { get; init; } = new Dictionary<string, string>();
}

public record ConnectionTestResultDto
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan? Latency { get; init; }
}

public record AddressNodeDto
{
    public required string Path { get; init; }
    public required string DisplayName { get; init; }
    public required string NodeType { get; init; }   // Folder / Variable
    public string? DataType { get; init; }
    public bool IsReadable { get; init; }
    public bool IsWritable { get; init; }
    public IReadOnlyList<AddressNodeDto>? Children { get; init; }

    public static AddressNodeDto Folder(string path, string name, params AddressNodeDto[] children) =>
        new() { Path = path, DisplayName = name, NodeType = "Folder", Children = children.Length == 0 ? null : children };

    public static AddressNodeDto Var(string path, string name, string dataType, bool writable = true) =>
        new() { Path = path, DisplayName = name, NodeType = "Variable", DataType = dataType, IsReadable = true, IsWritable = writable };
}

// ---------- 读 / 写 ----------

public record TagReadRequestDto
{
    public required string Address { get; init; }
    public string? DataType { get; init; }
}

public record VerificationExpectationDto
{
    public string ConfigurationFingerprint { get; init; } = "";
    public string? Revision { get; init; }
}

public record ReadTagsRequest
{
    public required IReadOnlyList<TagReadRequestDto> Tags { get; init; }
    public VerificationExpectationDto? Verification { get; init; }
}

public record VerificationEvidenceDto
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

public record TagValueDto
{
    public required string Address { get; init; }
    public required string DataType { get; init; }
    public required object Value { get; init; }
    public required string Quality { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string? ErrorMessage { get; init; }
}

public record ReadTagsResponse
{
    public required string DeviceId { get; init; }
    public required IReadOnlyList<TagValueDto> Tags { get; init; }
    public VerificationEvidenceDto? Evidence { get; init; }
}

public record WriteTagRequest
{
    public required string Address { get; init; }
    public required string DataType { get; init; }
    public required System.Text.Json.JsonElement Value { get; init; }
}

public record WriteTagsRequest
{
    public required IReadOnlyList<WriteTagRequest> Tags { get; init; }
}

public record WriteTagResultDto
{
    public required string Address { get; init; }
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public record WriteTagsResponse
{
    public required string DeviceId { get; init; }
    public int TotalCount { get; init; }
    public int SuccessCount { get; init; }
    public required IReadOnlyList<WriteTagResultDto> Results { get; init; }
}

// ---------- 批量连接验证 ----------

public record BatchTestRequest
{
    public IReadOnlyList<string>? DeviceIds { get; init; }
    public string? DeviceType { get; init; }
    public int? Concurrency { get; init; }
}

public record BatchTestResultDto
{
    public required string TestId { get; init; }
    public int TotalDevices { get; init; }
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public double SuccessRate { get; init; }
    public required IReadOnlyList<DeviceTestResultDto> Results { get; init; }
    public double DurationMs { get; init; }
}

public record DeviceTestResultDto
{
    public required string DeviceId { get; init; }
    public bool Success { get; init; }
    public double LatencyMs { get; init; }
    public string? ErrorMessage { get; init; }
}
