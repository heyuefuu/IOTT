using Microsoft.AspNetCore.Mvc;
using MachineConnectionApi.Proxy;
using MachineConnectionApi.Services;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MachineConnectionApi.Controllers;

/// <summary>
/// 地址空间 Web API：将请求转发至 Industrial IoT 服务。
/// </summary>
[ApiController]
[Route("api/addressspace")]
public class AddressSpaceController : IndustrialIoTProxyControllerBase
{
    private static readonly IReadOnlyList<AddressNode> NCLinkApiStandardTree = BuildNCLinkApiStandardTree();
    private static readonly JsonSerializerOptions CamelCaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly INCLinkModelProbeClient _modelProbeClient;
    private readonly ILogger<AddressSpaceController> _logger;

    public AddressSpaceController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        INCLinkModelProbeClient modelProbeClient,
        ILogger<AddressSpaceController> logger)
        : base(httpClientFactory, logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _modelProbeClient = modelProbeClient;
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
        var normalizedParentPath = NormalizeParentPath(parentPath);
        var effectiveProtocol = !string.IsNullOrWhiteSpace(protocol)
            ? protocol
            : await TryGetDeviceProtocolAsync(deviceId, ct);
        if (IsNCLinkApiProtocol(effectiveProtocol))
        {
            var ncLinkDeviceId = await TryGetNCLinkApiDeviceIdAsync(deviceId, ct) ?? deviceId;
            return await BrowseNCLinkApiAsync(ncLinkDeviceId, normalizedParentPath, ct);
        }

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

    private string BuildBrowsePath(string deviceId, string? parentPath)
    {
        return string.IsNullOrWhiteSpace(parentPath)
            ? $"{AddressSpacePath}/{Uri.EscapeDataString(deviceId)}"
            : $"{AddressSpacePath}/{Uri.EscapeDataString(deviceId)}?parentPath={Uri.EscapeDataString(parentPath)}";
    }

    private async Task<IActionResult> BrowseNCLinkApiAsync(
        string deviceId,
        string? parentPath,
        CancellationToken ct)
    {
        var probeJson = await _modelProbeClient.LoadModelJsonAsync(deviceId, ct);
        var modelJson = probeJson is null ? null : NormalizeNCLinkAddressSpaceJson(probeJson);
        if (modelJson is not null)
            return ApplyAddressSpaceImmediateChildrenFilter(parentPath, new ContentResult
            {
                StatusCode = StatusCodes.Status200OK,
                Content = modelJson,
                ContentType = "application/json",
            });

        string path;
        try
        {
            path = BuildNCLinkModelPath(deviceId);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented,
                new { error = "NC-Link 设备模型加载尚未配置", detail = ex.Message });
        }

        var client = _httpClientFactory.CreateClient("NCLinkApi");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NC-Link 地址空间转发失败: {Path}", path);
            return BrowseNCLinkApiStandardTree(parentPath);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return BrowseNCLinkApiStandardTree(parentPath);
            }

            modelJson = NormalizeNCLinkAddressSpaceJson(text);
            if (modelJson is null)
                return BrowseNCLinkApiStandardTree(parentPath);

            var result = new ContentResult
            {
                StatusCode = StatusCodes.Status200OK,
                Content = modelJson,
                ContentType = "application/json",
            };
            return ApplyAddressSpaceImmediateChildrenFilter(parentPath, result);
        }
    }

    private IActionResult BrowseNCLinkApiStandardTree(string? parentPath)
    {
        var fallback = JsonSerializer.Serialize(NCLinkApiStandardTree, CamelCaseJsonOptions);
        return ApplyAddressSpaceImmediateChildrenFilter(parentPath, new ContentResult
        {
            StatusCode = StatusCodes.Status200OK,
            Content = fallback,
            ContentType = "application/json",
        });
    }

    private static string? NormalizeParentPath(string? parentPath)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
            return null;

        var normalized = parentPath.Trim().Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);

        if (!normalized.StartsWith("/", StringComparison.Ordinal))
            normalized = "/" + normalized;

        return normalized;
    }

    private string BuildNCLinkModelPath(string deviceId)
    {
        var template = _configuration["NCLinkApi:ModelPathTemplate"] ?? "v1/{deviceId}/model";
        if (string.IsNullOrWhiteSpace(template))
            template = "v1/{deviceId}/model";

        var escapedDeviceId = Uri.EscapeDataString(deviceId.Trim());
        return template.Trim().TrimStart('/')
            .Replace("{deviceId}", escapedDeviceId, StringComparison.OrdinalIgnoreCase)
            .Replace("{device_id}", escapedDeviceId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNCLinkApiProtocol(string? protocol)
    {
        var v = (protocol ?? string.Empty).Trim();
        return v.Equals("NCLinkApi", StringComparison.OrdinalIgnoreCase);
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

    private async Task<string?> TryGetNCLinkApiDeviceIdAsync(string deviceId, CancellationToken ct)
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
            if (!TryGetProperty(doc.RootElement, "extendedProperties", out var props))
                return null;

            var realDeviceId = TryGetStringProperty(props, "DeviceId");
            return string.IsNullOrWhiteSpace(realDeviceId) ? null : realDeviceId.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsEmptyJsonArrayResult(IActionResult result)
    {
        if (result is not ContentResult content ||
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

    private static string? NormalizeNCLinkAddressSpaceJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var status = TryGetStringProperty(root, "status");
            var code = TryGetIntProperty(root, "code");
            if (code is not 0 ||
                !string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
                return null;

            var nodes = BuildNCLinkAddressNodes(value);
            return JsonSerializer.Serialize(nodes, CamelCaseJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool LooksLikeNCLinkApiFailure(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   TryGetStringProperty(root, "status") is { } status &&
                   !status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<AddressNode> BuildNCLinkAddressNodes(JsonElement root)
    {
        var nodes = new List<AddressNode>();
        AppendNCLinkModelNode(root, "", nodes);
        AddNCLinkApiStandardVariableLeaves(nodes);
        return nodes;
    }

    private static void AddNCLinkApiStandardVariableLeaves(List<AddressNode> nodes)
    {
        var existing = new HashSet<string>(nodes.Select(n => n.Path), StringComparer.OrdinalIgnoreCase);
        var folders = nodes
            .Where(n => string.Equals(n.NodeType, "Folder", StringComparison.OrdinalIgnoreCase)
                && NormalizeNCLinkModelPath(n.Path).Equals("/MACHINE/CONTROLLER/VARIABLE", StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Path.TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var folder in folders)
        {
            if (nodes.Any(n => n.Path.StartsWith(folder + "@", StringComparison.OrdinalIgnoreCase)))
                continue;

            foreach (var standard in NCLinkApiStandardTree
                .Where(n => n.Path.StartsWith("/MACHINE/CONTROLLER/VARIABLE@", StringComparison.OrdinalIgnoreCase)))
            {
                var path = folder + standard.Path["/MACHINE/CONTROLLER/VARIABLE".Length..];
                if (!existing.Add(path))
                    continue;

                nodes.Add(new AddressNode
                {
                    Path = path,
                    DisplayName = standard.DisplayName,
                    NodeType = standard.NodeType,
                    DataType = standard.DataType,
                    IsReadable = standard.IsReadable,
                    IsWritable = standard.IsWritable,
                });
            }
        }
    }

    private static string NormalizeNCLinkModelPath(string? path)
    {
        var normalized = NCLinkApiPathParser.NormalizePath(path ?? "");
        return normalized.Equals("/", StringComparison.Ordinal) ? normalized : normalized.TrimEnd('/');
    }

    private static void AppendNCLinkModelNode(JsonElement node, string parentPath, List<AddressNode> nodes)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;

        var segment = BuildNCLinkModelSegment(node);
        if (string.IsNullOrWhiteSpace(segment))
            return;

        var path = string.IsNullOrWhiteSpace(parentPath)
            ? $"/{segment}"
            : $"{parentPath.TrimEnd('/')}/{segment}";
        var hasChildren = HasNCLinkModelChildren(node);
        nodes.Add(new AddressNode
        {
            Path = path,
            DisplayName = BuildNCLinkModelDisplayName(node, segment),
            NodeType = hasChildren ? "Folder" : "Variable",
            DataType = hasChildren ? null : InferNCLinkDataType(node),
            IsReadable = true,
            IsWritable = IsNCLinkNodeWritable(node),
            SourceId = TryGetStringProperty(node, "id"),
        });

        foreach (var child in EnumerateNCLinkModelChildren(node))
            AppendNCLinkModelNode(child, path, nodes);
    }

    private static IEnumerable<JsonElement> EnumerateNCLinkModelChildren(JsonElement node)
    {
        foreach (var name in new[] { "devices", "configs", "dataItems", "components" })
        {
            if (!TryGetProperty(node, name, out var children) || children.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var child in children.EnumerateArray())
                yield return child;
        }
    }

    private static bool HasNCLinkModelChildren(JsonElement node)
    {
        foreach (var _ in EnumerateNCLinkModelChildren(node))
            return true;
        return false;
    }

    private static string BuildNCLinkModelSegment(JsonElement node)
    {
        var type = TryGetStringProperty(node, "type");
        if (string.IsNullOrWhiteSpace(type))
            return "";

        var number = TryGetStringProperty(node, "number");
        return string.IsNullOrWhiteSpace(number)
            ? type.Trim()
            : $"{type.Trim()}@{number.Trim()}";
    }

    private static string BuildNCLinkModelDisplayName(JsonElement node, string segment)
    {
        var name = TryGetStringProperty(node, "name");
        return string.IsNullOrWhiteSpace(name) ? segment : name.Trim();
    }

    private static bool IsNCLinkNodeWritable(JsonElement node)
    {
        var access = TryGetStringProperty(node, "access") ?? TryGetStringProperty(node, "permission");
        return access?.Contains("write", StringComparison.OrdinalIgnoreCase) == true ||
               access?.Contains("写", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string InferNCLinkDataType(JsonElement node)
    {
        foreach (var name in new[] { "dataType", "datatype", "valueType", "value_type" })
        {
            var value = TryGetStringProperty(node, name);
            if (!string.IsNullOrWhiteSpace(value))
                return NormalizeAddressDataType(value);
        }

        var type = TryGetStringProperty(node, "type");
        if (type?.Contains("WARNING", StringComparison.OrdinalIgnoreCase) == true ||
            type?.Contains("ALARM", StringComparison.OrdinalIgnoreCase) == true ||
            type?.Contains("STRING", StringComparison.OrdinalIgnoreCase) == true)
            return "String";

        return "String";
    }

    private static string NormalizeAddressDataType(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v is "bool" or "boolean" or "bit")
            return "Bool";
        if (v is "float" or "single" or "real")
            return "Float";
        if (v is "double")
            return "Double";
        if (v.Contains("int64") || v == "long")
            return "Int64";
        if (v.Contains("int16") || v == "short")
            return "Int16";
        if (v.Contains("int"))
            return "Int32";
        return "String";
    }

    private static IReadOnlyList<AddressNode> BuildNCLinkApiStandardTree() =>
    [
        Folder("/MACHINE", "MACHINE 机床根"),
        Var("/MACHINE/STATUS", "机床状态", "String", false),
        Var("/MACHINE/FEED_SPEED", "进给速度", "Double", false),
        Var("/MACHINE/FEED_OVERRIDE", "进给倍率", "Int32", false),
        Var("/MACHINE/SPDL_OVERRIDE", "主轴倍率", "Int32", false),
        Var("/MACHINE/SPINDLE_OVERRIDE", "主轴倍率(2.41+)", "Int32", false),
        Var("/MACHINE/PART_COUNT", "加工件数", "Int32", false),
        Var("/MACHINE/PROGRAM_NUMBER?index=1", "程序号(index=1)", "Int32", false),
        Folder("/MACHINE/CONTROLLER", "CONTROLLER 控制器"),
        Var("/MACHINE/CONTROLLER/PROGRAM", "当前程序", "String", false),
        Var("/MACHINE/CONTROLLER/WARNING", "报警信息", "String", false),
        Var("/MACHINE/CONTROLLER/PARAMETER?index=100", "参数(index=100)", "String", false),
        Var("/MACHINE/CONTROLLER/TOOL_PARAM?index=1", "刀具参数(index=1)", "String", false),
        Var("/MACHINE/CONTROLLER/COORDINATE?index=1", "坐标系(index=1)", "String", false),
        Var("/MACHINE/CONTROLLER/CONSOLE", "控制台指令", "String", true),
        Var("/MACHINE/CONTROLLER/FILE", "G 代码目录", "String", false),
        Folder("/MACHINE/CONTROLLER/VARIABLE", "VARIABLE 变量"),
        Var("/MACHINE/CONTROLLER/VARIABLE@SYS?index=11", "系统数据-NCK版本", "String", false),
        Var("/MACHINE/CONTROLLER/VARIABLE@SYS?index=25", "系统数据-装置SN", "String", false),
        Var("/MACHINE/CONTROLLER/VARIABLE@MACRO?index=10000", "宏变量", "Double", false),
        Var("/MACHINE/CONTROLLER/VARIABLE@REG_X?index=0", "寄存器 X", "Int32", true),
        Var("/MACHINE/CONTROLLER/VARIABLE@REG_Y?index=0", "寄存器 Y", "Int32", true),
        Var("/MACHINE/CONTROLLER/VARIABLE@REG_R?index=0", "寄存器 R", "Int32", true),
        Var("/MACHINE/CONTROLLER/VARIABLE@REG_F?index=0", "寄存器 F", "Int32", true),
        Var("/MACHINE/CONTROLLER/VARIABLE@REG_G?index=0", "寄存器 G", "Int32", true),
        Var("/MACHINE/CONTROLLER/VARIABLE@REG_B?index=0", "寄存器 B", "Int32", true),
        Var("/MACHINE/CONTROLLER/VARIABLE@CHAN_0?index=6", "通道0 指令进给速度", "Float", false),
        Var("/MACHINE/CONTROLLER/VARIABLE@AXIS_0?index=38", "X轴实际位置", "Double", false),
    ];

    private static AddressNode Folder(string path, string name) =>
        new() { Path = path, DisplayName = name, NodeType = "Folder", IsReadable = false, IsWritable = false };

    private static AddressNode Var(string path, string name, string dataType, bool writable) =>
        new() { Path = path, DisplayName = name, NodeType = "Variable", DataType = dataType, IsReadable = true, IsWritable = writable };

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

            currentParent = NormalizeParentPath(matched.Path);
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

    private static int? TryGetIntProperty(JsonElement item, string name)
    {
        if (!TryGetProperty(item, name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), out var n) => n,
            _ => null,
        };
    }

    private static bool TryGetProperty(JsonElement item, string name, out JsonElement value)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in item.EnumerateObject())
            {
                if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
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
