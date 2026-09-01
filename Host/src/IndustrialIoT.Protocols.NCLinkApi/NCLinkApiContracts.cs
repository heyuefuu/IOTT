namespace IndustrialIoT.Protocols.NCLinkApi;

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

// ── 请求 DTO ────────────────────────────────────────────────────────────

/// <summary>
/// 手册表 5-1 的通用请求体：
/// { "operation":"get_value", "items":[{"path":..., "index":..., "value":..., "offset":..., "key":..., "length":..., "timeout":...}] }
/// 一次请求可包含多个 item（手册 8.2）。
/// </summary>
public sealed class NCLinkApiRequest
{
    [JsonPropertyName("operation")]
    public required string Operation { get; init; }

    [JsonPropertyName("items")]
    public required IReadOnlyList<NCLinkApiRequestItem> Items { get; init; }
}

public sealed record NCLinkApiRequestItem
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>整数下标，或 int[]。手册允许标量或数组。</summary>
    [JsonPropertyName("index")]
    public JsonNode? Index { get; init; }

    [JsonPropertyName("value")]
    public JsonNode? Value { get; init; }

    [JsonPropertyName("offset")]
    public int? Offset { get; init; }

    /// <summary>手册：hash 类型的键（如文件名、采样通道字符串号）。string 或 string[]。</summary>
    [JsonPropertyName("key")]
    public JsonNode? Key { get; init; }

    [JsonPropertyName("length")]
    public int? Length { get; init; }

    [JsonPropertyName("timeout")]
    public int? Timeout { get; init; }
}

// ── 响应 DTO ────────────────────────────────────────────────────────────

/// <summary>
/// 手册第 9 章响应体：{ "status":"SUCCESS"|"FAILED", "code":0, "value":[[...],...] }
/// value 是嵌套数组：外层对应 items，内层对应 index 数组。
/// 设置/操作类响应内层是 [true]/[false]，查询类响应内层是数值/对象数组。
/// </summary>
public sealed class NCLinkApiResponse
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("code")]
    public int Code { get; init; }

    /// <summary>原始 value 节点，调用方按手册章节解释。</summary>
    [JsonPropertyName("value")]
    public JsonNode? Value { get; init; }

    public bool IsSuccess => Code == 0 &&
        Status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase);

    public NCLinkApiStatusCode StatusCode => (NCLinkApiStatusCode)Code;
}
