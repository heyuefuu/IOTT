using System.Text.Json.Nodes;

namespace MachineConnectionApi.Services;

public static class NCLinkApiPathParser
{
    private const string ModelRootPrefix = "/NC_LINK_ROOT";

    public static NCLinkApiRequestItem ToRequestItem(string raw, JsonNode? value = null)
    {
        var (path, index, offset, key, timeout) = Parse(raw);
        return new NCLinkApiRequestItem
        {
            Path = path,
            Index = index,
            Offset = offset,
            Key = key,
            Timeout = timeout,
            Value = value,
        };
    }

    public static (string Path, JsonNode? Index, int? Offset, JsonNode? Key, int? TimeoutMs) Parse(string raw)
    {
        raw = raw?.Trim() ?? "";
        var q = raw.IndexOf('?');
        if (q < 0)
            return (NormalizePath(raw), null, null, null, null);

        var path = NormalizePath(raw[..q]);
        var qs = System.Web.HttpUtility.ParseQueryString(raw[(q + 1)..]);
        return (path, ParseIndex(qs["index"]), ParseInt(qs["offset"]),
            ParseKey(qs["key"]), ParseInt(qs["timeout"]));
    }

    public static string NormalizePath(string rawPath)
    {
        var path = (rawPath ?? "").Trim().Replace('\\', '/');
        while (path.Contains("//", StringComparison.Ordinal))
            path = path.Replace("//", "/", StringComparison.Ordinal);
        if (!path.StartsWith('/'))
            path = "/" + path;

        if (path.Equals(ModelRootPrefix, StringComparison.OrdinalIgnoreCase))
            return "/";
        if (path.StartsWith(ModelRootPrefix + "/", StringComparison.OrdinalIgnoreCase))
            return path[ModelRootPrefix.Length..];
        if (path.StartsWith(ModelRootPrefix + "@", StringComparison.OrdinalIgnoreCase))
            return path[ModelRootPrefix.Length..];

        return path;
    }

    private static JsonNode? ParseIndex(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!raw.Contains(','))
            return int.TryParse(raw, out var value) ? JsonValue.Create(value) : JsonValue.Create(raw);

        var arr = new JsonArray();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(token, out var value))
                arr.Add(value);
        return arr;
    }

    private static JsonNode? ParseKey(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (!raw.Contains(',')) return JsonValue.Create(raw);

        var arr = new JsonArray();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            arr.Add(token);
        return arr;
    }

    private static int? ParseInt(string? raw) =>
        int.TryParse(raw, out var value) ? value : null;
}
