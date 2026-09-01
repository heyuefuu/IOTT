using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MachineConnectionApi.Services;

namespace MachineConnectionApi.Controllers;

/// <summary>
/// DatacollectionController 的协议分流实现 — 读取部分按 Protocol 字段分流：
/// IndustrialIoT 走上游 5173 的 api/data/{id}/read；NCLinkApi 走 nclink-api-server 19001 的 /v1/{id}/data/。
/// </summary>
public partial class DatacollectionController
{
    private async Task<string> ResolveNCLinkApiDeviceIdAsync(string uiDeviceId, CancellationToken ct)
    {
        var identity = await _deviceResolver.ResolveAsync(uiDeviceId, ct);
        if (!identity.IsNCLinkApi)
            return uiDeviceId;
        if (!identity.HasConfiguredDeviceId)
            throw new UpstreamReadException(StatusCodes.Status400BadRequest,
                "NC-Link API Server 须填写 DeviceId（机床 SN 码）", null);
        return identity.DeviceId;
    }

    private async Task<List<DatacollectionCollectItemDto>> ReadFromIndustrialIoTAsync(
        string deviceId, IReadOnlyList<DatacollectionPointDto> points, CancellationToken ct)
    {
        var payload = new
        {
            tags = points.Select(p => new { address = p.Path, dataType = MapDbDatatypeToReadType(p.Datatype) }),
        };
        var dataPath = _configuration["IndustrialIoT:DataPath"] ?? "api/data";
        var path = $"{dataPath}/{Uri.EscapeDataString(deviceId)}/read";
        var client = _httpClientFactory.CreateClient("IndustrialIoT");
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "采集转发失败: {Path}", path);
            throw new UpstreamReadException(StatusCodes.Status502BadGateway, "Industrial IoT 服务不可用", ex.Message);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new UpstreamReadException((int)response.StatusCode, "Industrial IoT 上游错误", body);

            ReadTagsProxyResponse? readResult;
            try
            {
                readResult = JsonSerializer.Deserialize<ReadTagsProxyResponse>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解析读值结果失败: {Body}", body);
                throw new UpstreamReadException(StatusCodes.Status502BadGateway, "解析 Industrial IoT 响应失败", ex.Message);
            }

            var tagMap = (readResult?.Tags ?? new List<ReadTagProxyItem>())
                .GroupBy(t => t.Address ?? "")
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            return points.Select(p =>
            {
                tagMap.TryGetValue(p.Path, out var t);
                var err = t?.ErrorMessage;
                var ok = string.IsNullOrWhiteSpace(err);
                return new DatacollectionCollectItemDto(
                    p.Name, p.Path, MapDbDatatypeToReadType(p.Datatype),
                    ok ? t?.Value : null, t?.Quality ?? "", t?.Timestamp ?? "",
                    ok ? "成功" : "失败", err,
                    p.CollectionFrequency <= 0 ? 500 : p.CollectionFrequency);
            }).ToList();
        }
    }

    private async Task<List<DatacollectionCollectItemDto>> ReadFromNCLinkApiAsync(
        string deviceId, IReadOnlyList<DatacollectionPointDto> points, CancellationToken ct)
    {
        var reqItems = points.Select(p =>
        {
            return NCLinkApiPathParser.ToRequestItem(p.Path);
        }).ToList();

        NCLinkApiResponse response;
        try { response = await _ncLinkApiClient.BatchGetValueAsync(deviceId, reqItems, ct); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NC-Link API 调用失败: deviceId={DeviceId}", deviceId);
            throw new UpstreamReadException(StatusCodes.Status502BadGateway, "NC-Link API 服务不可用", ex.Message);
        }

        if (!response.IsSuccess)
            throw new UpstreamReadException(StatusCodes.Status502BadGateway,
                $"NC-Link 上游返回失败: {response.Status}", $"code={response.Code}");

        var values = response.Value as JsonArray ?? new JsonArray();
        var ts = DateTimeOffset.UtcNow.ToString("o");
        return points.Select((p, i) =>
        {
            object? value = null;
            string status = "成功";
            string? err = null;
            if (i < values.Count && values[i] is JsonArray inner && inner.Count > 0)
                value = ExtractScalar(inner[0]);
            else { status = "失败"; err = "上游返回值缺失"; }
            return new DatacollectionCollectItemDto(
                p.Name, p.Path, MapDbDatatypeToReadType(p.Datatype),
                value, "Good", ts, status, err,
                p.CollectionFrequency <= 0 ? 500 : p.CollectionFrequency);
        }).ToList();
    }

    private static object? ExtractScalar(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<bool>(out var b)) return b;
            if (jv.TryGetValue<long>(out var l)) return l;
            if (jv.TryGetValue<double>(out var d)) return d;
            if (jv.TryGetValue<string>(out var s)) return s;
        }
        return node.ToJsonString();
    }

    private static string NormalizeProtocol(string? raw)
    {
        var v = (raw ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(v)) return "IndustrialIoT";
        if (v.Equals("NCLinkApi", StringComparison.OrdinalIgnoreCase))
            return "NCLinkApi";
        return "IndustrialIoT";
    }
}
