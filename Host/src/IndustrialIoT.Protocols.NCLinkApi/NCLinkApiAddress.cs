namespace IndustrialIoT.Protocols.NCLinkApi;

using System.Collections.Specialized;
using System.Text.Json.Nodes;
using System.Web;

/// <summary>
/// 地址编码工具 — 把 NC-Link API 的 path+index+offset+key+timeout 五元组
/// 编码成单个 string，以兼容 IProtocolDriver 的 string address 接口。
///
/// 编码格式：path[?index=N|index=N1,N2,...][&offset=M][&key=X][&timeout=T]
/// 示例：
///   "/MACHINE/STATUS"
///   "/MACHINE/CONTROLLER/VARIABLE@REG_X?index=10"
///   "/MACHINE/CONTROLLER/VARIABLE@REG_X?index=0,1,2,3"
///   "/MACHINE/CONTROLLER/VARIABLE@REG_G?index=2960&offset=12"
///   "/MACHINE/CONTROLLER/FILE?key=O0001"
/// </summary>
public sealed record NCLinkApiAddress
{
    public required string Path { get; init; }
    public JsonNode? Index { get; init; }
    public int? Offset { get; init; }
    public JsonNode? Key { get; init; }
    public int? TimeoutMs { get; init; }

    public static NCLinkApiAddress Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("address required", nameof(address));

        var qIdx = address.IndexOf('?');
        if (qIdx < 0)
            return new NCLinkApiAddress { Path = address };

        var path = address[..qIdx];
        var query = HttpUtility.ParseQueryString(address[(qIdx + 1)..]);

        return new NCLinkApiAddress
        {
            Path = path,
            Index = ParseIndex(query["index"]),
            Offset = ParseInt(query["offset"]),
            Key = ParseKey(query["key"]),
            TimeoutMs = ParseInt(query["timeout"]),
        };
    }

    public NCLinkApiRequestItem ToRequestItem() => new()
    {
        Path = Path,
        Index = Index,
        Offset = Offset,
        Key = Key,
        Timeout = TimeoutMs,
    };

    /// <summary>index 支持 "10" 或 "0,1,2,3"。</summary>
    private static JsonNode? ParseIndex(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.Contains(','))
        {
            var arr = new JsonArray();
            foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(token, out var v)) arr.Add(v);
            return arr;
        }
        return int.TryParse(raw, out var single) ? JsonValue.Create(single) : JsonValue.Create(raw);
    }

    /// <summary>key 支持 "O0001" 或 "O0001,O0002"（按手册 8.1.18）。</summary>
    private static JsonNode? ParseKey(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (raw.Contains(','))
        {
            var arr = new JsonArray();
            foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                arr.Add(token);
            return arr;
        }
        return JsonValue.Create(raw);
    }

    private static int? ParseInt(string? raw) =>
        int.TryParse(raw, out var v) ? v : null;
}
