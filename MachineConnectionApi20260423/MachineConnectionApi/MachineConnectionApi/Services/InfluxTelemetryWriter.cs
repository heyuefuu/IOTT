using System.Text.Json;
using InfluxDB.Client;
using InfluxDB.Client.Core.Exceptions;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;
using MachineConnectionApi.Options;
using Microsoft.Extensions.Options;

namespace MachineConnectionApi.Services;

public interface IInfluxTelemetryWriter
{
    Task WriteBatchAsync(
        string deviceId,
        DateTimeOffset batchTime,
        IReadOnlyList<InfluxTelemetryPoint> points,
        CancellationToken ct = default);
}

public sealed record InfluxTelemetryPoint(
    string Name,
    string Path,
    string DataType,
    object? Value,
    string Quality,
    string Timestamp,
    string Status,
    string? ErrorMessage);

public sealed class InfluxTelemetryWriter : IInfluxTelemetryWriter, IDisposable
{
    private readonly IOptionsMonitor<InfluxDbOptions> _options;
    private readonly ILogger<InfluxTelemetryWriter> _logger;
    private readonly object _gate = new();
    private InfluxDBClient? _client;

    public InfluxTelemetryWriter(
        IOptionsMonitor<InfluxDbOptions> options,
        ILogger<InfluxTelemetryWriter> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task WriteBatchAsync(
        string deviceId,
        DateTimeOffset batchTime,
        IReadOnlyList<InfluxTelemetryPoint> points,
        CancellationToken ct = default)
    {
        var opt = _options.CurrentValue;
        if (!opt.Enabled || points.Count == 0)
            return;

        if (string.IsNullOrWhiteSpace(opt.Token) ||
            string.IsNullOrWhiteSpace(opt.Org) ||
            string.IsNullOrWhiteSpace(opt.Bucket))
        {
            _logger.LogWarning("InfluxDB 已启用但 Token/Org/Bucket 未配置完整，跳过写入");
            return;
        }

        var client = GetOrCreateClient(opt);
        var measurement = string.IsNullOrWhiteSpace(opt.Measurement)
            ? "datapoint"
            : opt.Measurement.Trim();

        var influxPoints = new List<PointData>(points.Count);
        foreach (var p in points)
        {
            var ts = ResolveTimestamp(p.Timestamp, batchTime);
            var pathTag = SanitizeTagValue(p.Path, maxLen: 240);
            var nameTag = SanitizeTagValue(p.Name, maxLen: 120);
            var statusTag = p.Status == "成功" ? "ok" : "fail";

            var point = PointData.Measurement(measurement)
                .Tag("device_id", SanitizeTagValue(deviceId, 120))
                .Tag("path", pathTag)
                .Tag("point_name", nameTag)
                .Tag("data_type", SanitizeTagValue(p.DataType, 60))
                .Tag("status", statusTag)
                .Field("quality", p.Quality ?? "");

            var err = p.ErrorMessage?.Trim();
            if (!string.IsNullOrEmpty(err))
                point = point.Field("error", TruncateField(err, 1024));

            point = AddValueFields(point, p.Value);
            point = point.Timestamp(ts, WritePrecision.Ms);
            influxPoints.Add(point);
        }

        await WriteWithRetryAsync(client, influxPoints, opt, deviceId, ct);
    }

    private InfluxDBClient GetOrCreateClient(InfluxDbOptions opt)
    {
        lock (_gate)
        {
            if (_client != null)
                return _client;

            var url = string.IsNullOrWhiteSpace(opt.Url) ? "http://localhost:8086" : opt.Url.Trim();
            var options = new InfluxDBClientOptions.Builder()
                .Url(url)
                .AuthenticateToken(opt.Token)
                .TimeOut(TimeSpan.FromSeconds(Math.Max(5, opt.WriteTimeoutSeconds)))
                .Build();

            _client = new InfluxDBClient(options);
            return _client;
        }
    }

    private async Task WriteWithRetryAsync(
        InfluxDBClient client,
        IReadOnlyList<PointData> influxPoints,
        InfluxDbOptions opt,
        string deviceId,
        CancellationToken ct)
    {
        var retries = Math.Max(0, opt.WriteRetryCount);
        var retryDelayMs = Math.Max(100, opt.WriteRetryDelayMs);

        for (var attempt = 1; attempt <= retries + 1; attempt++)
        {
            try
            {
                var writeApi = client.GetWriteApiAsync();
                await writeApi.WritePointsAsync(influxPoints, opt.Bucket, opt.Org, ct);
                return;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt <= retries)
            {
                _logger.LogWarning(ex,
                    "写入 InfluxDB 超时，将重试: attempt={Attempt}/{MaxAttempt}, deviceId={DeviceId}, points={Count}, timeoutSec={TimeoutSec}",
                    attempt,
                    retries + 1,
                    deviceId,
                    influxPoints.Count,
                    Math.Max(5, opt.WriteTimeoutSeconds));
                await Task.Delay(retryDelayMs, ct);
            }
            catch (HttpException ex) when (attempt <= retries)
            {
                _logger.LogWarning(ex,
                    "写入 InfluxDB 失败，将重试: attempt={Attempt}/{MaxAttempt}, deviceId={DeviceId}, points={Count}",
                    attempt,
                    retries + 1,
                    deviceId,
                    influxPoints.Count);
                await Task.Delay(retryDelayMs, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "写入 InfluxDB 失败: deviceId={DeviceId}, points={Count}, url={Url}, bucket={Bucket}, org={Org}",
                    deviceId,
                    influxPoints.Count,
                    opt.Url,
                    opt.Bucket,
                    opt.Org);
                throw;
            }
        }

        throw new InvalidOperationException("写入 InfluxDB 重试后仍失败。");
    }

    private static DateTimeOffset ResolveTimestamp(string? isoFromDevice, DateTimeOffset fallback)
    {
        if (!string.IsNullOrWhiteSpace(isoFromDevice) &&
            DateTimeOffset.TryParse(isoFromDevice, out var parsed))
            return parsed;

        return fallback;
    }

    private static string SanitizeTagValue(string? value, int maxLen)
    {
        var s = (value ?? string.Empty).Replace('\r', '_').Replace('\n', '_').Trim();
        if (s.Length <= maxLen)
            return string.IsNullOrEmpty(s) ? "_" : s;
        return s[..maxLen];
    }

    private static string TruncateField(string s, int max) =>
        s.Length <= max ? s : s[..max];

    private static PointData AddValueFields(PointData point, object? value)
    {
        switch (value)
        {
            case null:
                return point.Field("value_present", false);
            case JsonElement el:
                return AddJsonElementFields(point, el);
            case bool b:
                return point.Field("value_present", true).Field("value_b", b);
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return point.Field("value_present", true).Field("value_i", Convert.ToInt64(value));
            case float f:
                return point.Field("value_present", true).Field("value_f", (double)f);
            case double d:
                return point.Field("value_present", true).Field("value_f", d);
            case decimal m:
                return point.Field("value_present", true).Field("value_f", (double)m);
            case string s:
                return point.Field("value_present", true).Field("value_s", TruncateField(s, 2048));
            default:
                return point.Field("value_present", true)
                    .Field("value_s", TruncateField(value.ToString() ?? "", 2048));
        }
    }

    private static PointData AddJsonElementFields(PointData point, JsonElement el)
    {
        if (el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return point.Field("value_present", false);

        point = point.Field("value_present", true);
        switch (el.ValueKind)
        {
            case JsonValueKind.True:
            case JsonValueKind.False:
                return point.Field("value_b", el.GetBoolean());
            case JsonValueKind.String:
                return point.Field("value_s", TruncateField(el.GetString() ?? "", 2048));
            case JsonValueKind.Number:
                if (el.TryGetInt64(out var l))
                    return point.Field("value_i", l);
                if (el.TryGetDouble(out var d))
                    return point.Field("value_f", d);
                return point.Field("value_s", el.GetRawText());
            default:
                return point.Field("value_s", TruncateField(el.GetRawText(), 2048));
        }
    }

    public void Dispose() => _client?.Dispose();
}
