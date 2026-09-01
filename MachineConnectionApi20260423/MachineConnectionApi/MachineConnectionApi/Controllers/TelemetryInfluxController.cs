using MachineConnectionApi.Options;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MachineConnectionApi.Controllers;

[ApiController]
[Route("api/telemetry/influx")]
public class TelemetryInfluxController : ControllerBase
{
    public const string InfluxHttpClientName = "InfluxDb3";

    private readonly IInfluxTelemetryWriter _writer;
    private readonly IMqttTelemetryPublisher _mqttPublisher;
    private readonly IOptionsSnapshot<InfluxDbOptions> _influxOptions;
    private readonly IOptionsSnapshot<MqttOptions> _mqttOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TelemetryInfluxController> _logger;

    public TelemetryInfluxController(
        IInfluxTelemetryWriter writer,
        IMqttTelemetryPublisher mqttPublisher,
        IOptionsSnapshot<InfluxDbOptions> influxOptions,
        IOptionsSnapshot<MqttOptions> mqttOptions,
        IHttpClientFactory httpClientFactory,
        ILogger<TelemetryInfluxController> logger)
    {
        _writer = writer;
        _mqttPublisher = mqttPublisher;
        _influxOptions = influxOptions;
        _mqttOptions = mqttOptions;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>将一批采集点写入 InfluxDB（与界面「数据采集」每轮 read 结果对应）。</summary>
    [HttpPost("batch")]
    public async Task<IActionResult> WriteBatch(
        [FromBody] InfluxTelemetryBatchRequest? body,
        CancellationToken ct)
    {
        if (body == null || string.IsNullOrWhiteSpace(body.DeviceId))
            return BadRequest(new { error = "deviceId 必填" });

        var pts = body.Points ?? new List<InfluxTelemetryBatchPoint>();
        if (pts.Count == 0)
            return Ok(new { written = 0, mqttPublished = false });

        var influxOn = _influxOptions.Value.Enabled;
        var mqttOn = _mqttOptions.Value.Enabled;
        if (!influxOn && !mqttOn)
        {
            return Ok(new
            {
                written = 0,
                mqttPublished = false,
                skipped = true,
                reason = "InfluxDB 与 Mqtt 均未启用（appsettings 中 InfluxDB:Enabled / Mqtt:Enabled）",
            });
        }

        var mapped = new List<InfluxTelemetryPoint>(pts.Count);
        foreach (var p in pts)
        {
            mapped.Add(new InfluxTelemetryPoint(
                p.Name ?? "",
                p.Path ?? "",
                p.DataType ?? "",
                p.Value,
                p.Quality ?? "",
                p.Timestamp ?? "",
                p.Status ?? "成功",
                p.ErrorMessage));
        }

        var deviceId = body.DeviceId.Trim();
        var batchTime = body.CollectedAt ?? DateTimeOffset.UtcNow;

        if (influxOn)
        {
            try
            {
                await _writer.WriteBatchAsync(deviceId, batchTime, mapped, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Influx batch API 写入失败");
                return StatusCode(StatusCodes.Status502BadGateway,
                    new { error = "InfluxDB 写入失败", detail = ex.Message });
            }
        }

        if (mqttOn)
        {
            try
            {
                await _mqttPublisher.PublishCollectionBatchAsync(deviceId, batchTime, mapped, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT 发布失败: deviceId={DeviceId}", deviceId);
                if (!influxOn)
                {
                    return StatusCode(StatusCodes.Status502BadGateway,
                        new { error = "MQTT 发布失败", detail = ex.Message });
                }

                return Ok(new
                {
                    written = mapped.Count,
                    mqttPublished = false,
                    mqttError = ex.Message,
                });
            }
        }

        return Ok(new
        {
            written = influxOn ? mapped.Count : 0,
            mqttPublished = mqttOn,
        });
    }

    /// <summary>查询 InfluxDB 历史采集记录。</summary>
    [HttpGet("history")]
    public async Task<IActionResult> QueryHistory(
        [FromQuery] string? deviceId,
        [FromQuery] DateTimeOffset? startTime,
        [FromQuery] DateTimeOffset? endTime,
        [FromQuery] string? path,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var id = deviceId?.Trim();
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest(new { error = "deviceId 必填" });

        if (page <= 0) page = 1;
        if (pageSize <= 0) pageSize = 20;
        if (pageSize > 200) pageSize = 200;

        var opt = _influxOptions.Value;
        if (!opt.Enabled)
            return Ok(new InfluxTelemetryHistoryPageResult());

        if (string.IsNullOrWhiteSpace(opt.Token) ||
            string.IsNullOrWhiteSpace(opt.Org) ||
            string.IsNullOrWhiteSpace(opt.Bucket))
        {
            return Ok(new InfluxTelemetryHistoryPageResult());
        }

        var start = startTime ?? DateTimeOffset.UtcNow.AddDays(-1);
        var stop = endTime ?? DateTimeOffset.UtcNow;
        if (stop <= start)
            return BadRequest(new { error = "endTime 必须大于 startTime" });

        var measurement = string.IsNullOrWhiteSpace(opt.Measurement)
            ? "datapoint"
            : opt.Measurement.Trim();
        var whereClause = BuildHistoryWhereClause(id, start, stop, path);
        var dataSql = BuildHistoryDataSql(measurement, whereClause, page, pageSize);
        var countSql = BuildHistoryCountSql(measurement, whereClause);

        try
        {
            var rows = await QueryHistoryFromInfluxDb3Async(opt, id, dataSql, ct);
            var total = await QueryHistoryCountFromInfluxDb3Async(opt, countSql, ct);
            return Ok(new InfluxTelemetryHistoryPageResult
            {
                Items = rows,
                Total = total,
                Page = page,
                PageSize = pageSize,
            });
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 库/表尚未建立（无任何数据写入）时 InfluxDB3 返回 404，按空结果处理
            _logger.LogWarning("Influx 历史查询返回 404，按空结果处理: deviceId={DeviceId}", id);
            return Ok(new InfluxTelemetryHistoryPageResult
            {
                Items = new List<InfluxTelemetryHistoryItem>(),
                Total = 0,
                Page = page,
                PageSize = pageSize,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Influx 历史查询失败: deviceId={DeviceId}", id);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "InfluxDB 历史查询失败", detail = ex.Message });
        }
    }

    private static string EscapeFluxString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string EscapeSqlString(string value) =>
        value.Replace("'", "''");

    private static string BuildHistoryWhereClause(
        string deviceId,
        DateTimeOffset start,
        DateTimeOffset stop,
        string? path)
    {
        var where = new List<string>
        {
            $"device_id = '{EscapeSqlString(deviceId)}'",
            $"time >= TIMESTAMP '{start.UtcDateTime:O}'",
            $"time <= TIMESTAMP '{stop.UtcDateTime:O}'",
        };
        if (!string.IsNullOrWhiteSpace(path))
        {
            where.Add($"path = '{EscapeSqlString(path.Trim())}'");
        }
        return string.Join(" AND ", where);
    }

    private static string BuildHistoryDataSql(
        string measurement,
        string whereClause,
        int page,
        int pageSize)
    {
        var offset = (page - 1) * pageSize;
        return $@"
SELECT
  time,
  device_id,
  path,
  point_name,
  data_type,
  status,
  quality,
  error,
  value_s,
  value_i,
  value_f,
  value_b,
  value_present
FROM {measurement}
WHERE {whereClause}
ORDER BY time DESC, path ASC, point_name ASC, data_type ASC, status ASC
LIMIT {pageSize}
OFFSET {offset}";
    }

    private static string BuildHistoryCountSql(string measurement, string whereClause)
    {
        return $@"
SELECT COUNT(*) AS total
FROM {measurement}
WHERE {whereClause}";
    }

    private async Task<List<InfluxTelemetryHistoryItem>> QueryHistoryFromInfluxDb3Async(
        InfluxDbOptions opt,
        string deviceId,
        string sql,
        CancellationToken ct)
    {
        using var response = await SendInfluxDb3QueryAsync(opt, sql, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return new List<InfluxTelemetryHistoryItem>();

        var rows = new List<InfluxTelemetryHistoryItem>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var timeRaw = GetJsonString(row, "time");
            rows.Add(new InfluxTelemetryHistoryItem
            {
                DeviceId = GetJsonString(row, "device_id", deviceId),
                Name = GetJsonString(row, "point_name"),
                Path = GetJsonString(row, "path"),
                DataType = GetJsonString(row, "data_type"),
                Value = ResolveJsonValue(row),
                Quality = GetJsonString(row, "quality"),
                Status = GetJsonString(row, "status"),
                ErrorMessage = GetJsonStringOrNull(row, "error"),
                Time = DateTimeOffset.TryParse(timeRaw, out var t) ? t : DateTimeOffset.UtcNow,
            });
        }
        return rows;
    }

    /// <summary>经 IHttpClientFactory 命名客户端查询 InfluxDB3 SQL 接口；连接池化复用，避免每请求新建 HttpClient 耗尽 socket。</summary>
    private async Task<HttpResponseMessage> SendInfluxDb3QueryAsync(
        InfluxDbOptions opt, string sql, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(opt.Url) ? "http://localhost:8086" : opt.Url.TrimEnd('/');
        var db = string.IsNullOrWhiteSpace(opt.Bucket) ? "machine_collection" : opt.Bucket.Trim();
        var uri = $"{baseUrl}/api/v3/query_sql?db={Uri.EscapeDataString(db)}&q={Uri.EscapeDataString(sql)}";

        var httpClient = _httpClientFactory.CreateClient(InfluxHttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opt.Token);
        return await httpClient.SendAsync(request, ct);
    }

    private async Task<long> QueryHistoryCountFromInfluxDb3Async(
        InfluxDbOptions opt,
        string sql,
        CancellationToken ct)
    {
        using var response = await SendInfluxDb3QueryAsync(opt, sql, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return 0;

        var row = doc.RootElement[0];
        if (!row.TryGetProperty("total", out var total))
            return 0;

        if (total.ValueKind == JsonValueKind.Number && total.TryGetInt64(out var n))
            return n;
        if (long.TryParse(total.ToString(), out var parsed))
            return parsed;
        return 0;
    }

    private static string GetJsonString(JsonElement row, string key, string fallback = "")
    {
        if (!row.TryGetProperty(key, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return fallback;
        return v.ToString() ?? fallback;
    }

    private static string? GetJsonStringOrNull(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        var s = v.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static object? ResolveJsonValue(JsonElement row)
    {
        if (row.TryGetProperty("value_present", out var vp) &&
            vp.ValueKind == JsonValueKind.False)
        {
            return null;
        }

        if (row.TryGetProperty("value_s", out var s) && s.ValueKind != JsonValueKind.Null)
            return s.ToString();
        if (row.TryGetProperty("value_i", out var i) && i.ValueKind == JsonValueKind.Number)
        {
            if (i.TryGetInt64(out var li)) return li;
            if (i.TryGetDouble(out var di)) return di;
        }
        if (row.TryGetProperty("value_f", out var f) && f.ValueKind == JsonValueKind.Number)
        {
            if (f.TryGetDouble(out var d)) return d;
        }
        if (row.TryGetProperty("value_b", out var b) &&
            (b.ValueKind == JsonValueKind.True || b.ValueKind == JsonValueKind.False))
        {
            return b.GetBoolean();
        }
        return null;
    }

    private static string ToFluxTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O");

    private static string GetString(IReadOnlyDictionary<string, object> values, string key)
    {
        if (!values.TryGetValue(key, out var raw) || raw == null)
            return string.Empty;
        return raw.ToString() ?? string.Empty;
    }

    private static DateTimeOffset ResolveRecordTime(IReadOnlyDictionary<string, object> values)
    {
        if (!values.TryGetValue("_time", out var raw) || raw == null)
            return DateTimeOffset.UtcNow;

        if (raw is DateTimeOffset dto) return dto;
        if (raw is DateTime dt) return new DateTimeOffset(dt.ToUniversalTime());
        if (DateTimeOffset.TryParse(raw.ToString(), out var parsed)) return parsed;
        return DateTimeOffset.UtcNow;
    }

    private static object? ResolveRecordValue(IReadOnlyDictionary<string, object> values)
    {
        if (values.TryGetValue("value_present", out var presentRaw) &&
            presentRaw is bool present &&
            !present)
        {
            return null;
        }

        if (values.TryGetValue("value_s", out var s) && s != null) return s;
        if (values.TryGetValue("value_i", out var i) && i != null) return i;
        if (values.TryGetValue("value_f", out var f) && f != null) return f;
        if (values.TryGetValue("value_b", out var b) && b != null) return b;
        return null;
    }
}

public class InfluxTelemetryBatchRequest
{
    public string DeviceId { get; set; } = "";

    public DateTimeOffset? CollectedAt { get; set; }

    public List<InfluxTelemetryBatchPoint>? Points { get; set; }
}

public class InfluxTelemetryBatchPoint
{
    public string Name { get; set; } = "";

    public string Path { get; set; } = "";

    public string DataType { get; set; } = "";

    /// <summary>读到的原始值；JSON 反序列化后可能为 JsonElement。</summary>
    public object? Value { get; set; }

    public string Quality { get; set; } = "";

    public string Timestamp { get; set; } = "";

    public string Status { get; set; } = "";

    public string? ErrorMessage { get; set; }
}

public class InfluxTelemetryHistoryItem
{
    public string DeviceId { get; set; } = "";

    public string Name { get; set; } = "";

    public string Path { get; set; } = "";

    public string DataType { get; set; } = "";

    public object? Value { get; set; }

    public string Quality { get; set; } = "";

    public string Status { get; set; } = "";

    public string? ErrorMessage { get; set; }

    public DateTimeOffset Time { get; set; }
}

public class InfluxTelemetryHistoryPageResult
{
    public List<InfluxTelemetryHistoryItem> Items { get; set; } = new();

    public long Total { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 20;
}
