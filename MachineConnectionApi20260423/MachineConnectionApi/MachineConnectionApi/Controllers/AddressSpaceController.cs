using Microsoft.AspNetCore.Mvc;
using MachineConnectionApi.Proxy;
using MachineConnectionApi.Services;
using System.IO.Compression;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;

namespace MachineConnectionApi.Controllers;

/// <summary>
/// 地址空间 Web API：将请求转发至 Industrial IoT 服务。
/// </summary>
[ApiController]
[Route("api/addressspace")]
public class AddressSpaceController : IndustrialIoTProxyControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AddressSpaceController> _logger;

    public AddressSpaceController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AddressSpaceController> logger)
        : base(httpClientFactory, logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string AddressSpacePath => _configuration["IndustrialIoT:AddressSpacePath"] ?? "api/addressspace";

    /// <summary>浏览设备地址空间</summary>
    [HttpGet("{deviceId}")]
    public async Task<IActionResult> Browse(
        string deviceId,
        [FromQuery] string? parentPath,
        [FromQuery] string? protocol,
        CancellationToken ct)
    {
        var effectiveProtocol = !string.IsNullOrWhiteSpace(protocol)
            ? protocol
            : await TryGetDeviceProtocolAsync(deviceId, ct);
        // OPC UA NodeIds are opaque identifiers, not filesystem paths.
        if (string.Equals(effectiveProtocol?.Trim(), "OpcUa", StringComparison.OrdinalIgnoreCase))
        {
            return await ProxyTextAsync(HttpMethod.Get, BuildBrowsePath(deviceId, parentPath), null, ct);
        }

        var normalizedParentPath = NormalizeParentPath(parentPath);
        var path = BuildBrowsePath(deviceId, normalizedParentPath);
        var directResult = await ProxyTextAsync(HttpMethod.Get, path, null, ct);

        // 当传入路径格式不标准（如反斜杠、缺少前导 /）导致直接查询为空时，
        // 按截图中的层级方式递归逐级定位目标目录后重查一遍。
        if (!string.IsNullOrWhiteSpace(normalizedParentPath) &&
            IsEmptyJsonArrayResult(directResult) &&
            await TryResolveParentPathRecursivelyAsync(deviceId, normalizedParentPath, ct) is { } resolvedPath &&
            !string.Equals(resolvedPath, normalizedParentPath, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "AddressSpace parentPath 已自动修正: {Original} -> {Resolved}",
                normalizedParentPath,
                resolvedPath);

            var retryPath = BuildBrowsePath(deviceId, resolvedPath);
            var retryResult = await ProxyTextAsync(HttpMethod.Get, retryPath, null, ct);
            return ApplyAddressSpaceImmediateChildrenFilter(resolvedPath, retryResult);
        }

        return ApplyAddressSpaceImmediateChildrenFilter(normalizedParentPath, directResult);
    }

    /// <summary>导出设备地址空间</summary>
    [HttpGet("{deviceId}/export")]
    public Task<IActionResult> Export(string deviceId, [FromQuery] string? format, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(format)
            ? $"{AddressSpacePath}/{Uri.EscapeDataString(deviceId)}/export"
            : $"{AddressSpacePath}/{Uri.EscapeDataString(deviceId)}/export?format={Uri.EscapeDataString(format)}";

        return ProxyFileAsync(HttpMethod.Get, path, null, ct);
    }

    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>导出地址空间为 Excel(.xlsx)：取上游 CSV 导出后转换为 xlsx（可选公司/项目元数据头）。</summary>
    [HttpGet("{deviceId}/export.xlsx")]
    public async Task<IActionResult> ExportXlsx(
        string deviceId,
        [FromQuery] string? company,
        [FromQuery] string? project,
        CancellationToken ct)
    {
        var (ok, csv, error) = await TryFetchHostCsvAsync(deviceId, ct);
        if (!ok) return StatusCode(StatusCodes.Status502BadGateway, new { error });
        var rows = WithMetadata(ParseCsv(csv), deviceId, company, project);
        var bytes = ExcelBuilder.Build("地址空间", rows);
        return File(bytes, XlsxContentType, $"address_space_{deviceId}.xlsx");
    }

    /// <summary>批量导出多台设备的地址空间为 zip（每台一个 xlsx，按设备名分文件）。</summary>
    [HttpGet("export-batch.zip")]
    public async Task<IActionResult> ExportBatch(
        [FromQuery] string deviceIds,
        [FromQuery] string? company,
        [FromQuery] string? project,
        CancellationToken ct)
    {
        var ids = (deviceIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0)
            return BadRequest(new { error = "deviceIds 不能为空" });

        using var buffer = new MemoryStream();
        var exported = 0;
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var id in ids)
            {
                var (ok, csv, _) = await TryFetchHostCsvAsync(id, ct);
                if (!ok) continue;
                var rows = WithMetadata(ParseCsv(csv), id, company, project);
                var bytes = ExcelBuilder.Build("地址空间", rows);
                var entry = zip.CreateEntry($"address_space_{SanitizeXmlName(id)}.xlsx");
                using var entryStream = entry.Open();
                entryStream.Write(bytes);
                exported++;
            }
        }

        if (exported == 0)
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "所有设备地址导出均失败或无数据" });

        return File(buffer.ToArray(), "application/zip",
            $"address_space_batch_{DateTimeOffset.Now:yyyyMMddHHmmss}.zip");
    }

    private static List<IReadOnlyList<object?>> WithMetadata(
        List<IReadOnlyList<object?>> rows,
        string deviceId,
        string? company,
        string? project)
    {
        if (string.IsNullOrWhiteSpace(company) && string.IsNullOrWhiteSpace(project))
            return rows;

        var withMeta = new List<IReadOnlyList<object?>>
        {
            new object?[] { "公司", company ?? "" },
            new object?[] { "项目", project ?? "" },
            new object?[] { "设备", deviceId },
            new object?[] { "导出时间", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss") },
            new object?[] { },
        };
        withMeta.AddRange(rows);
        return withMeta;
    }

    /// <summary>导出地址空间为 XML：取上游 CSV 导出后转换为 XML。</summary>
    [HttpGet("{deviceId}/export.xml")]
    public async Task<IActionResult> ExportXml(string deviceId, CancellationToken ct)
    {
        var (ok, csv, error) = await TryFetchHostCsvAsync(deviceId, ct);
        if (!ok) return StatusCode(StatusCodes.Status502BadGateway, new { error });
        var xml = BuildXml(ParseCsv(csv));
        return File(Encoding.UTF8.GetBytes(xml), "application/xml", $"address_space_{deviceId}.xml");
    }

    private async Task<(bool Ok, string Csv, string? Error)> TryFetchHostCsvAsync(string deviceId, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("IndustrialIoT");
            var path = $"{AddressSpacePath}/{Uri.EscapeDataString(deviceId)}/export?format=csv";
            using var response = await client.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode)
                return (false, "", $"上游导出失败：HTTP {(int)response.StatusCode}");
            var text = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text))
                return (false, "", "上游导出为空（该协议可能不支持导出）");
            return (true, text, null);
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    private static List<IReadOnlyList<object?>> ParseCsv(string text)
    {
        var rows = new List<IReadOnlyList<object?>>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            rows.Add(SplitCsvLine(raw).Cast<object?>().ToList());
        }
        return rows;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    private static string BuildXml(List<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<AddressSpace>\n");
        if (rows.Count > 0)
        {
            var headers = rows[0].Select(h => SanitizeXmlName(h?.ToString() ?? "")).ToList();
            for (var r = 1; r < rows.Count; r++)
            {
                sb.Append("  <Node>\n");
                var cells = rows[r];
                for (var c = 0; c < headers.Count && c < cells.Count; c++)
                {
                    var value = SecurityElement.Escape(cells[c]?.ToString() ?? "") ?? "";
                    sb.Append($"    <{headers[c]}>{value}</{headers[c]}>\n");
                }
                sb.Append("  </Node>\n");
            }
        }
        sb.Append("</AddressSpace>");
        return sb.ToString();
    }

    private static string SanitizeXmlName(string name)
    {
        var cleaned = new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
        if (string.IsNullOrEmpty(cleaned)) cleaned = "Field";
        if (char.IsDigit(cleaned[0])) cleaned = "F" + cleaned;
        return cleaned;
    }

    private string BuildBrowsePath(string deviceId, string? parentPath)
    {
        return string.IsNullOrWhiteSpace(parentPath)
            ? $"{AddressSpacePath}/{Uri.EscapeDataString(deviceId)}"
            : $"{AddressSpacePath}/{Uri.EscapeDataString(deviceId)}?parentPath={Uri.EscapeDataString(parentPath)}";
    }

    private static string? NormalizeParentPath(string? parentPath)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
            return null;

        var normalized = parentPath.Trim().Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);

        return normalized;
    }

    private async Task<string?> TryGetDeviceProtocolAsync(string deviceId, CancellationToken ct)
    {
        try
        {
            var devicesPath = _configuration["IndustrialIoT:DevicesPath"] ?? "api/Devices";
            var client = _httpClientFactory.CreateClient("IndustrialIoT");
            using var response = await client.GetAsync(
                $"{devicesPath}/{Uri.EscapeDataString(deviceId)}", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return TryGetStringProperty(doc.RootElement, "protocol");
        }
        catch
        {
            return null;
        }
    }

    private static bool IsEmptyJsonArrayResult(IActionResult result)
    {
        if (result is not ContentResult content ||
            (content.StatusCode ?? StatusCodes.Status200OK) is < 200 or >= 300 ||
            string.IsNullOrWhiteSpace(content.Content))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(content.Content);
            return doc.RootElement.ValueKind == JsonValueKind.Array &&
                   doc.RootElement.GetArrayLength() == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> TryResolveParentPathRecursivelyAsync(
        string deviceId,
        string normalizedParentPath,
        CancellationToken ct)
    {
        var segments = normalizedParentPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return null;

        string? currentParent = null;
        foreach (var segment in segments)
        {
            var levelNodes = await QueryAddressSpaceLevelAsync(deviceId, currentParent, ct);
            if (levelNodes.Count == 0)
                return null;

            var matched = levelNodes.FirstOrDefault(x =>
                string.Equals(x.DisplayName, segment, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(GetPathLeafName(x.Path), segment, StringComparison.OrdinalIgnoreCase));
            if (matched == null || string.IsNullOrWhiteSpace(matched.Path))
                return null;

            currentParent = matched.Path;
        }

        return currentParent;
    }

    private async Task<List<AddressNode>> QueryAddressSpaceLevelAsync(
        string deviceId,
        string? parentPath,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("IndustrialIoT");
        var requestPath = BuildBrowsePath(deviceId, parentPath);
        using var response = await client.GetAsync(requestPath, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return new List<AddressNode>();

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        try
        {
            var nodes = JsonSerializer.Deserialize<List<AddressNode>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var list = nodes ?? new List<AddressNode>();
            return FilterAddressNodesToImmediateChildren(parentPath, list);
        }
        catch
        {
            return new List<AddressNode>();
        }
    }

    /// <summary>
    /// Industrial IoT 有时在任意 parentPath 下返回整表扁平数组；按 path 只保留当前层直接子节点，便于前端树懒加载。
    /// </summary>
    private IActionResult ApplyAddressSpaceImmediateChildrenFilter(string? parentPath, IActionResult result)
    {
        if (result is not ContentResult cr || string.IsNullOrWhiteSpace(cr.Content))
            return result;

        var status = cr.StatusCode ?? StatusCodes.Status200OK;
        if (status < 200 || status >= 300)
            return result;

        var filtered = TryFilterAddressSpaceJsonImmediateChildren(
            parentPath, cr.Content, out var totalCount, out var keptCount, out var droppedSamples);
        if (filtered is null)
            return result;

        if (_logger.IsEnabled(LogLevel.Information))
        {
            var dropped = totalCount - keptCount;
            _logger.LogInformation(
                "AddressSpace 过滤 parent='{Parent}' 上游={Total} 留下={Kept} 丢弃={Dropped}{DroppedSample}",
                parentPath ?? "/",
                totalCount,
                keptCount,
                dropped,
                droppedSamples.Count > 0 ? $" 样例丢弃=[{string.Join(", ", droppedSamples)}]" : "");
        }

        return new ContentResult
        {
            StatusCode = status,
            Content = filtered,
            ContentType = cr.ContentType ?? "application/json",
        };
    }

    /// <summary>
    /// 将上游返回的「扁平」地址表过滤为当前 parent 下的直接子节点。使用 JsonDocument + Utf8JsonWriter
    /// 重新写出数组，避免用 JsonNode 在多个 JsonArray 间复用同一节点（会抛 NodeAlreadyHasParent，法那科 FOCAS 常触发）。
    /// </summary>
    private static string? TryFilterAddressSpaceJsonImmediateChildren(
        string? normalizedParentPath, string json,
        out int totalCount, out int keptCount, out IReadOnlyList<string> droppedSamples)
    {
        totalCount = 0;
        keptCount = 0;
        droppedSamples = Array.Empty<string>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return null;

            var withPath = 0;
            foreach (var el in root.EnumerateArray())
            {
                var p = GetPathFromAddressSpaceItem(el);
                if (string.IsNullOrWhiteSpace(p))
                    continue;
                withPath++;
                var t = p.Trim();
                if (!(t.StartsWith('/') && !t.StartsWith("//", StringComparison.Ordinal)))
                    return null;
            }

            if (withPath == 0)
                return null;

            var dropped = new List<string>(4);
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
            {
                writer.WriteStartArray();
                foreach (var el in root.EnumerateArray())
                {
                    var p = GetPathFromAddressSpaceItem(el);
                    if (string.IsNullOrWhiteSpace(p))
                        continue;
                    totalCount++;
                    if (!IsSlashStyleImmediateChild(normalizedParentPath, p.Trim()))
                    {
                        if (dropped.Count < 3) dropped.Add(p.Trim());
                        continue;
                    }
                    keptCount++;
                    el.WriteTo(writer);
                }
                writer.WriteEndArray();
            }

            droppedSamples = dropped;
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetPathFromAddressSpaceItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        return TryGetStringProperty(item, "path");
    }

    private static string? TryGetStringProperty(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var prop in item.EnumerateObject())
        {
            if (!prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            return prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
        }

        return null;
    }

    private static List<AddressNode> FilterAddressNodesToImmediateChildren(
        string? parentPath,
        List<AddressNode> nodes)
    {
        if (nodes.Count == 0)
            return nodes;

        if (!nodes.All(n =>
            {
                var t = n.Path?.Trim() ?? "";
                return t.Length == 0
                    || (t.StartsWith('/') && !t.StartsWith("//", StringComparison.Ordinal));
            }))
            return nodes;

        return nodes
            .Where(n => !string.IsNullOrWhiteSpace(n.Path)
                && IsSlashStyleImmediateChild(parentPath, n.Path.Trim()))
            .ToList();
    }

    private static string CollapsePathSlashes(string path)
    {
        var n = path.Trim().Replace('\\', '/');
        while (n.Contains("//", StringComparison.Ordinal))
            n = n.Replace("//", "/", StringComparison.Ordinal);
        return n.TrimEnd('/');
    }

    private static bool IsSlashStyleImmediateChild(string? normalizedParentPath, string childPath)
    {
        var c = CollapsePathSlashes(childPath);
        if (string.IsNullOrEmpty(c))
            return false;

        if (string.IsNullOrWhiteSpace(normalizedParentPath) || normalizedParentPath == "/")
        {
            if (c.StartsWith("//", StringComparison.Ordinal))
            {
                var body = c[2..];
                return body.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 1;
            }

            if (c.StartsWith('/'))
                return c.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 1;

            return true;
        }

        var p = CollapsePathSlashes(normalizedParentPath);
        if (!p.StartsWith('/'))
            p = "/" + p;
        if (string.Equals(c, p, StringComparison.Ordinal))
            return false;

        if (p.StartsWith("//", StringComparison.Ordinal))
        {
            if (!c.StartsWith("//", StringComparison.Ordinal))
                return false;
            var prefix = p.EndsWith('/') ? p : $"{p}/";
            if (!c.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            var rel = c[prefix.Length..];
            return rel.Length > 0 && !rel.Contains('/');
        }

        if (p.StartsWith('/'))
        {
            // 备用分隔符：@ 用于 NC-Link 变量子域 (VARIABLE@SYS)，. / : 用于广数 GSK (Realtime.Mode, Macro:100)
            foreach (var delimiter in new[] { '@', '.', ':' })
            {
                var altPrefix = p + delimiter;
                if (!c.StartsWith(altPrefix, StringComparison.Ordinal))
                    continue;
                var altRel = c[altPrefix.Length..];
                return altRel.Length > 0 && !altRel.Contains('/');
            }

            var prefix = p.EndsWith('/') ? p : $"{p}/";
            if (!c.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            var rel = c[prefix.Length..];
            return rel.Length > 0 && !rel.Contains('/');
        }

        return true;
    }

    private static string GetPathLeafName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = path.Replace('\\', '/').Trim('/');
        var idx = normalized.LastIndexOf("/", StringComparison.Ordinal);
        return idx >= 0 ? normalized[(idx + 1)..] : normalized;
    }
}

public sealed class AddressNode
{
    public string Path { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string NodeType { get; set; } = "Variable";
    public string? DataType { get; set; }
    public bool IsReadable { get; set; }
    public bool IsWritable { get; set; }
    public string? SourceId { get; set; }
}
