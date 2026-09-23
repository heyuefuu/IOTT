namespace MachineConnectionApi.Services;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MachineConnectionApi.Controllers;
using MachineConnectionApi.Models;
using MachineConnectionApi.Options;

public sealed class InfluxConnectionTester(IHttpClientFactory httpClientFactory)
{
    public async Task<InfluxConnectionTestResult> TestAsync(InfluxDbOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Token))
            return new(false, "请填写访问令牌 Token 后测试连接。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var table = InfluxSettingsStore.QuoteMeasurement(options.Measurement);
            var schemaSql = "SELECT COUNT(*) AS table_count FROM information_schema.tables " +
                $"WHERE table_schema = 'iox' AND table_name = '{options.Measurement}'";
            using var schema = await QueryAsync(options, schemaSql, timeout.Token);
            if (schema.RootElement.ValueKind != JsonValueKind.Array || schema.RootElement.GetArrayLength() != 1 ||
                schema.RootElement[0].ValueKind != JsonValueKind.Object ||
                !schema.RootElement[0].TryGetProperty("table_count", out var count) ||
                count.ValueKind != JsonValueKind.Number || !count.TryGetInt64(out var tables))
                return new(false, "服务返回格式不符合 InfluxDB 3 SQL 接口，请检查地址和版本。");
            if (tables == 0)
                return new(true, "连接成功，目标数据库可读取，尚无采集表；首次采集写入后将自动建表。");

            using var sample = await QueryAsync(options, $"SELECT * FROM {table} LIMIT 1", timeout.Token);
            if (sample.RootElement.ValueKind != JsonValueKind.Array)
                return new(false, "采集表查询返回格式异常，请检查 InfluxDB 版本。");
            return new(true, sample.RootElement.GetArrayLength() == 0
                ? "连接成功，采集表可读取，当前尚无数据。"
                : "连接成功，采集表和历史数据可读取。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, "连接测试超时，请检查历史库地址、端口和网络。");
        }
        catch (HttpRequestException ex)
        {
            return new(false, DescribeFailure(ex.StatusCode));
        }
        catch (JsonException)
        {
            return new(false, "服务返回格式不符合 InfluxDB 3 SQL 接口，请检查地址和版本。");
        }
    }

    private async Task<JsonDocument> QueryAsync(InfluxDbOptions options, string sql, CancellationToken ct)
    {
        var url = $"{options.Url.TrimEnd('/')}/api/v3/query_sql?db={Uri.EscapeDataString(options.Bucket)}" +
            $"&q={Uri.EscapeDataString(sql)}&format=json";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        var client = httpClientFactory.CreateClient(TelemetryInfluxController.InfluxHttpClientName);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public static string DescribeFailure(HttpStatusCode? status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "认证失败，请检查 Token 及目标数据库的读取权限。",
        HttpStatusCode.NotFound =>
            "未找到 InfluxDB 3 SQL 接口或目标数据库，请检查地址、版本和数据库名称。",
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
            "SQL 查询失败，请确认服务为 InfluxDB 3 且目标数据库配置正确。",
        null => "连接失败，请确认 InfluxDB 已启动，并检查服务地址、端口和网络。",
        _ => $"历史库返回 HTTP {(int)status.Value}，请检查服务状态。",
    };
}
