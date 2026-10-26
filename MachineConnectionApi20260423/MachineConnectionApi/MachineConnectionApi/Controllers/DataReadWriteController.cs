using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MachineConnectionApi.Models;
using MachineConnectionApi.Proxy;
using MachineConnectionApi.Services;

namespace MachineConnectionApi.Controllers;

/// <summary>
/// 数据读写 Web API：将请求转发至 Industrial IoT 服务；写入时记录操作审计（原值/新值/操作人/结果）。
/// </summary>
[ApiController]
[Route("api/data")]
public class DataReadWriteController : IndustrialIoTProxyControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IDeviceStore _deviceStore;
    private readonly DeviceOperationLogStore _operationLog;
    private readonly IHttpClientFactory _httpClientFactory;

    public DataReadWriteController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IDeviceStore deviceStore,
        DeviceOperationLogStore operationLog,
        ILogger<DataReadWriteController> logger)
        : base(httpClientFactory, logger)
    {
        _configuration = configuration;
        _deviceStore = deviceStore;
        _operationLog = operationLog;
        _httpClientFactory = httpClientFactory;
    }

    private string DataPath => _configuration["IndustrialIoT:DataPath"] ?? "api/data";

    /// <summary>批量读取点位</summary>
    [HttpPost("{deviceId}/read")]
    public async Task<IActionResult> ReadTags(string deviceId, CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        var content = CreateJsonContent(body);
        var path = $"{DataPath}/{Uri.EscapeDataString(deviceId)}/read";
        return await ProxyTextAsync(HttpMethod.Post, path, content, ct);
    }

    /// <summary>批量写入点位（写入前后尽力采集原值，记录审计历史）</summary>
    [HttpPost("{deviceId}/write")]
    public async Task<IActionResult> WriteTags(string deviceId, CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        var tags = ParseWriteTags(body);
        var oldValues = tags.Count > 0
            ? await TryReadOldValuesAsync(deviceId, tags, ct)
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var content = CreateJsonContent(body);
        var path = $"{DataPath}/{Uri.EscapeDataString(deviceId)}/write";
        var result = await ProxyTextAsync(HttpMethod.Post, path, content, ct);

        if (tags.Count > 0)
            RecordWriteAudit(deviceId, tags, oldValues, result);

        return result;
    }

    private static List<(string Address, string DataType, string? NewValue)> ParseWriteTags(string body)
    {
        var list = new List<(string, string, string?)>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var tag in tags.EnumerateArray())
            {
                var address = tag.TryGetProperty("address", out var a) ? a.GetString() : null;
                if (string.IsNullOrWhiteSpace(address)) continue;
                var dataType = tag.TryGetProperty("dataType", out var d) ? d.GetString() : null;
                string? value = null;
                if (tag.TryGetProperty("value", out var v))
                    value = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
                list.Add((address!, dataType ?? "", value));
            }
        }
        catch (JsonException) { /* 非预期体结构：跳过审计 */ }
        return list;
    }

    private async Task<Dictionary<string, string?>> TryReadOldValuesAsync(
        string deviceId,
        List<(string Address, string DataType, string? NewValue)> tags,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                tags = tags.Select(t => new
                {
                    address = t.Address,
                    dataType = string.IsNullOrWhiteSpace(t.DataType) ? "String" : t.DataType,
                }),
            });
            var client = _httpClientFactory.CreateClient("IndustrialIoT");
            var readPath = $"{DataPath}/{Uri.EscapeDataString(deviceId)}/read";
            using var response = await client.PostAsync(
                readPath, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            if (!response.IsSuccessStatusCode) return result;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("values", out var vs) && vs.ValueKind == JsonValueKind.Array
                    ? vs.EnumerateArray()
                    : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tags", out var ts) && ts.ValueKind == JsonValueKind.Array
                        ? ts.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var address = item.TryGetProperty("address", out var a) ? a.GetString() : null;
                if (string.IsNullOrWhiteSpace(address)) continue;
                if (item.TryGetProperty("value", out var v))
                    result[address!] = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
            }
        }
        catch { /* 原值采集为尽力而为，失败不影响写入 */ }
        return result;
    }

    private void RecordWriteAudit(
        string deviceId,
        List<(string Address, string DataType, string? NewValue)> tags,
        Dictionary<string, string?> oldValues,
        IActionResult result)
    {
        var success = result is ContentResult content
            ? (content.StatusCode ?? StatusCodes.Status200OK) is >= 200 and < 300
            : true;
        var error = success
            ? null
            : (result as ContentResult)?.Content;
        var device = _deviceStore.ReadAll().FirstOrDefault(x => x.Id == deviceId);
        var user = HttpContext.Items["mc.username"] as string ?? "unknown";
        var now = DateTimeOffset.Now;

        _operationLog.Append(tags.Select(tag => new DeviceOperationLogDto
        {
            Id = Guid.NewGuid().ToString("N"),
            DeviceId = deviceId,
            DeviceName = device?.Name ?? deviceId,
            DeviceType = device?.Type ?? "",
            Address = tag.Address,
            DataType = tag.DataType,
            OldValue = oldValues.TryGetValue(tag.Address, out var old) ? old : null,
            NewValue = tag.NewValue,
            Success = success,
            Error = error,
            User = user,
            Timestamp = now,
        }));
    }
}
