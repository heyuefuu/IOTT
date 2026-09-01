using Microsoft.AspNetCore.Mvc;
using MachineConnectionApi.Proxy;
using MachineConnectionApi.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MachineConnectionApi.Controllers;

/// <summary>
/// 数据读写 Web API：将请求转发至 Industrial IoT 服务。
/// </summary>
[ApiController]
[Route("api/data")]
public class DataReadWriteController : IndustrialIoTProxyControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly INCLinkApiClient _ncLinkApiClient;
    private readonly INCLinkMqttQueryClient _ncLinkMqttQueryClient;
    private readonly INCLinkDeviceResolver _deviceResolver;

    public DataReadWriteController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        INCLinkApiClient ncLinkApiClient,
        INCLinkMqttQueryClient ncLinkMqttQueryClient,
        INCLinkDeviceResolver deviceResolver,
        ILogger<DataReadWriteController> logger)
        : base(httpClientFactory, logger)
    {
        _configuration = configuration;
        _ncLinkApiClient = ncLinkApiClient;
        _ncLinkMqttQueryClient = ncLinkMqttQueryClient;
        _deviceResolver = deviceResolver;
    }

    private string DataPath => _configuration["IndustrialIoT:DataPath"] ?? "api/data";

    /// <summary>批量读取点位</summary>
    [HttpPost("{deviceId}/read")]
    public async Task<IActionResult> ReadTags(string deviceId, CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        var identity = await _deviceResolver.ResolveAsync(deviceId, ct);
        if (identity.IsNCLinkApi)
        {
            if (!identity.HasConfiguredDeviceId)
                return BadRequest(new { error = "NC-Link API Server 须填写 DeviceId（机床 SN 码）" });
            return await ReadNCLinkTagsAsync(deviceId, identity.DeviceId, body, ct);
        }

        var content = CreateJsonContent(body);
        var path = $"{DataPath}/{Uri.EscapeDataString(deviceId)}/read";
        return await ProxyTextAsync(HttpMethod.Post, path, content, ct);
    }

    /// <summary>批量写入点位</summary>
    [HttpPost("{deviceId}/write")]
    public async Task<IActionResult> WriteTags(string deviceId, CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        var identity = await _deviceResolver.ResolveAsync(deviceId, ct);
        if (identity.IsNCLinkApi)
        {
            if (!identity.HasConfiguredDeviceId)
                return BadRequest(new { error = "NC-Link API Server 须填写 DeviceId（机床 SN 码）" });
            return await WriteNCLinkTagsAsync(deviceId, identity.DeviceId, body, ct);
        }

        var content = CreateJsonContent(body);
        var path = $"{DataPath}/{Uri.EscapeDataString(deviceId)}/write";
        return await ProxyTextAsync(HttpMethod.Post, path, content, ct);
    }

    private async Task<IActionResult> ReadNCLinkTagsAsync(
        string uiDeviceId, string ncLinkDeviceId, string body, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<ReadTagsRequestDto>(
            body, JsonOptions);
        if (request?.Tags is null || request.Tags.Count == 0)
            return BadRequest(new { error = "tags required" });

        var mqttResult = await TryReadNCLinkTagsBySourceIdAsync(uiDeviceId, ncLinkDeviceId, request.Tags, ct);
        if (mqttResult is not null)
            return mqttResult;

        var items = request.Tags.Select(t => ToNCLinkItem(t.Address, null)).ToList();
        var response = await _ncLinkApiClient.BatchGetValueAsync(ncLinkDeviceId, items, ct);
        if (!response.IsSuccess)
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = $"NC-Link 上游返回失败: {response.Status}", detail = $"code={response.Code}" });

        var values = response.Value as JsonArray ?? new JsonArray();
        var timestamp = DateTimeOffset.UtcNow.ToString("o");
        var tags = request.Tags.Select((t, i) =>
        {
            object? value = null;
            string? error = null;
            if (i < values.Count)
                value = ExtractNCLinkValue(values[i]);
            else
                error = "上游返回值缺失";

            return new ReadTagResultDto(t.Address, t.DataType, value,
                error is null ? "Good" : "Bad", timestamp, error);
        }).ToList();
        return Ok(new ReadTagsResponseDto(uiDeviceId, tags));
    }

    private async Task<IActionResult> WriteNCLinkTagsAsync(
        string uiDeviceId, string ncLinkDeviceId, string body, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<WriteTagsRequestDto>(
            body, JsonOptions);
        if (request?.Tags is null || request.Tags.Count == 0)
            return BadRequest(new { error = "tags required" });

        var items = request.Tags
            .Select(t => ToNCLinkItem(t.Address, JsonSerializer.SerializeToNode(t.Value)))
            .ToList();
        var response = await _ncLinkApiClient.BatchDataAsync(
            ncLinkDeviceId, "set_value", items, ct);
        var timestamp = DateTimeOffset.UtcNow.ToString("o");
        var ok = response.IsSuccess;
        var results = request.Tags.Select(t => new WriteTagResultDto(
            t.Address,
            ok,
            ok ? null : $"NC-Link 上游返回失败: {response.Status}, code={response.Code}",
            timestamp)).ToList();

        return Ok(new WriteTagsResponseDto(
            uiDeviceId,
            results.Count,
            results.Count(r => r.Success),
            results));
    }

    private async Task<IActionResult?> TryReadNCLinkTagsBySourceIdAsync(
        string uiDeviceId,
        string ncLinkDeviceId,
        IReadOnlyList<ReadTagRequestDto> tags,
        CancellationToken ct)
    {
        if (tags.Any(t => string.IsNullOrWhiteSpace(t.SourceId)))
            return null;

        var response = await _ncLinkMqttQueryClient.QueryAsync(
            ncLinkDeviceId,
            tags.Select(t => t.SourceId!.Trim()).ToList(),
            ct);
        if (response is null)
            return null;

        var timestamp = DateTimeOffset.UtcNow.ToString("o");
        var results = tags.Select((t, i) =>
        {
            var valueNode = i < response.Values.GetArrayLength()
                ? response.Values[i]
                : default;
            var code = TryGetStringProperty(valueNode, "code");
            var ok = string.Equals(code, "OK", StringComparison.OrdinalIgnoreCase);
            var value = ok ? ExtractNCLinkMqttValue(valueNode) : null;
            return new ReadTagResultDto(t.Address, t.DataType, value,
                ok ? "Good" : "Bad", timestamp, ok ? null : $"NC-Link MQTT Query failed: {code}");
        }).ToList();

        return Ok(new ReadTagsResponseDto(uiDeviceId, results));
    }

    private static NCLinkApiRequestItem ToNCLinkItem(string address, JsonNode? value)
    {
        return NCLinkApiPathParser.ToRequestItem(address, value);
    }

    private static object? ExtractNCLinkMqttValue(JsonElement item)
    {
        if (!item.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            return null;
        if (values.GetArrayLength() == 0)
            return null;
        return ExtractJsonElementValue(values[0]);
    }

    private static object? ExtractJsonElementValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var l) => l,
            JsonValueKind.Number when value.TryGetDouble(out var d) => d,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value.GetRawText(),
        };

    private static string? TryGetStringProperty(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var prop in item.EnumerateObject())
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.GetRawText();
        return null;
    }

    private static object? ExtractNCLinkValue(JsonNode? node)
    {
        if (node is JsonArray arr && arr.Count > 0)
            node = arr[0];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var b)) return b;
            if (value.TryGetValue<long>(out var l)) return l;
            if (value.TryGetValue<double>(out var d)) return d;
            if (value.TryGetValue<string>(out var s)) return s;
        }
        return node?.ToJsonString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

public sealed record ReadTagsRequestDto(List<ReadTagRequestDto> Tags);
public sealed record ReadTagRequestDto(string Address, string DataType, string? SourceId = null);

public sealed record ReadTagsResponseDto(
    string DeviceId,
    IReadOnlyList<ReadTagResultDto> Tags);

public sealed record ReadTagResultDto(
    string Address,
    string DataType,
    object? Value,
    string Quality,
    string Timestamp,
    string? ErrorMessage);

public sealed record WriteTagsRequestDto(List<WriteTagRequestDto> Tags);
public sealed record WriteTagRequestDto(string Address, string DataType, object? Value);

public sealed record WriteTagsResponseDto(
    string DeviceId,
    int TotalCount,
    int SuccessCount,
    IReadOnlyList<WriteTagResultDto> Results);

public sealed record WriteTagResultDto(
    string Address,
    bool Success,
    string? ErrorMessage,
    string Timestamp);
