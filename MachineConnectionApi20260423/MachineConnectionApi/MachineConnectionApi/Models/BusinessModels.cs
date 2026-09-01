namespace MachineConnectionApi.Models;

public sealed record SystemLogDto
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string User { get; init; }
    public required string Action { get; init; }
    public required string Ip { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Detail { get; init; }
}

public sealed record AppUserDto
{
    public required string Id { get; init; }
    public required string Username { get; init; }
    public required string Name { get; init; }
    public string Password { get; init; } = "";
    public required string Role { get; init; }
    public required string Status { get; init; }
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public string LastLogin { get; init; } = "";

    /// <summary>PBKDF2 凭据（iterations.saltBase64.hashBase64）；仅存储用，对外接口一律置 null</summary>
    public string? PasswordHash { get; init; }
}

public sealed record LoginRequest(string Username, string Password);

public sealed record LoginUserInfo(string Id, string Username, string Name, string Role, IReadOnlyList<string> Permissions);

public sealed record LoginResponse(string Token, LoginUserInfo User);

public sealed record PermissionDto(string Key, string Name);

public sealed record MetricDto
{
    public required string Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public string Unit { get; init; } = "";
    public string StatusLabel { get; init; } = "待定义";
    public string StatusType { get; init; } = "info";
    public string Description { get; init; } = "";
    public string Reference { get; init; } = "";
    /// <summary>达标阈值（实测值 ≥ 阈值判为达标）；为空时自动验证退回内置默认判据</summary>
    public double? Threshold { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record ReportTemplateDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string Description { get; init; } = "";
    public string Content { get; init; } = "";
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record TemplatePreviewResponse(string Html);

public sealed record SystemStatusDto
{
    public required string Uptime { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CurrentTime { get; init; }
}

public sealed record VerifyTaskDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string Status { get; init; } = "pending";
    public string Priority { get; init; } = "中";
    public string DeviceId { get; init; } = "";
    public string MachineId { get; init; } = "";
    public IReadOnlyList<string> MetricIds { get; init; } = [];
    public string Params { get; init; } = "";
    public string Description { get; init; } = "";
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string ExecutionTime { get; init; } = "";
    public string Result { get; init; } = "";
    public string Detail { get; init; } = "";

    /// <summary>定时执行：none = 手动，daily = 每天 ScheduleTime（HH:mm）自动执行</summary>
    public string ScheduleType { get; init; } = "none";
    public string ScheduleTime { get; init; } = "";
    public DateTimeOffset? LastAutoRunAt { get; init; }

    /// <summary>最近一次运行的完整结果（VerifyRunResponse JSON），Excel 导出与结果回看的数据源</summary>
    public string LastRunJson { get; init; } = "";
}

public sealed record DeviceImportResult(int Total, int Success, int Failed, IReadOnlyList<string> Errors);

public sealed record TransferDeviceDto
{
    public required string Protocol { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public int? ConnectTimeoutMs { get; init; }
    public int? ReadTimeoutMs { get; init; }
    public Dictionary<string, string>? ExtendedProperties { get; init; }
}

public sealed record MachineDeviceDto
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
    public int ConnectTimeoutMs { get; init; } = 10000;
    public int ReadTimeoutMs { get; init; } = 5000;
    public Dictionary<string, string> ExtendedProperties { get; init; } = [];
    public TransferDeviceDto? Transfer { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>最近一次向上游 Industrial IoT 注册表同步是否成功（null = 尚未同步过）</summary>
    public bool? UpstreamSynced { get; init; }
    public string? UpstreamError { get; init; }
}

/// <summary>
/// 创建/更新设备的请求体。Id、Status、CreatedAt 由服务端生成或维护，故**不**出现在这里 ——
/// 直接拿 <see cref="MachineDeviceDto"/> 收请求会因这三个 required 属性让 System.Text.Json
/// 在进入控制器前就抛 400（"was missing required properties"），而方法体里它们又会被立即覆盖掉。
/// Status 不接受客户端指定：新建恒为 Offline，之后由连接测试/采集服务维护。
/// 全部可空是为了让 PUT 支持部分更新：未提供的字段沿用现有值。
/// </summary>
public sealed record MachineDeviceUpsertRequest
{
    public string? Name { get; init; }
    public string? Type { get; init; }
    public string? Brand { get; init; }
    public string? Model { get; init; }
    public string? Protocol { get; init; }
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? Username { get; init; }
    public int? ConnectTimeoutMs { get; init; }
    public int? ReadTimeoutMs { get; init; }
    public Dictionary<string, string>? ExtendedProperties { get; init; }
    public TransferDeviceDto? Transfer { get; init; }
}
