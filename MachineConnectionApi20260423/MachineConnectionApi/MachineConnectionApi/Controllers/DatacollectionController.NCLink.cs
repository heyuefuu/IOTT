using System.Text;
using System.Text.Json;

namespace MachineConnectionApi.Controllers;

public partial class DatacollectionController
{
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
                var err = t is null ? "Upstream value missing" : t.ErrorMessage;
                if (t is not null && string.IsNullOrWhiteSpace(err) &&
                    !string.Equals(t.Quality, "Good", StringComparison.OrdinalIgnoreCase))
                    err = $"Upstream value quality: {t.Quality}";
                var ok = string.IsNullOrWhiteSpace(err);
                return new DatacollectionCollectItemDto(
                    p.Name, p.Path, MapDbDatatypeToReadType(p.Datatype),
                    ok ? t?.Value : null, t?.Quality ?? "", t?.Timestamp ?? "",
                    ok ? "成功" : "失败", err,
                    p.CollectionFrequency <= 0 ? 500 : p.CollectionFrequency);
            }).ToList();
        }
    }

}
