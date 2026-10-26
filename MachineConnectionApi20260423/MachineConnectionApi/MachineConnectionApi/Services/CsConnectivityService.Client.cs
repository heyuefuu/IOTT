using System.Diagnostics;
using System.Net.Sockets;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

/// <summary>CsConnectivityService 客户端部分：真实 TCP 连通性探测与数据源周期循环。</summary>
public sealed partial class CsConnectivityService
{
    /// <summary>真实发起一次 TCP 连接以验证目标可达性，返回连接耗时（RTT）。</summary>
    public async Task<CsProbeResult> ProbeTcpAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535)
            return new CsProbeResult(false, 0, "主机或端口无效", Now());

        using var client = new TcpClient();
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, timeoutCts.Token);
            sw.Stop();
            return new CsProbeResult(true, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                $"连接成功 {host}:{port}", Now());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new CsProbeResult(false, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                $"连接超时（>{timeoutMs}ms）", Now());
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CsProbeResult(false, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                $"连接失败：{ex.Message}", Now());
        }
    }

    /// <summary>数据源探测：Modbus 发送真实 FC03 读取报文并解析；REST 发送 GET 并解析状态行；其余为连通性探测。</summary>
    private async Task<CsProbeResult> ProbeDataSourceAsync(CsGateway gw, CsDataSource ds, CancellationToken ct)
    {
        if (string.Equals(gw.Type, "Modbus", StringComparison.OrdinalIgnoreCase) &&
            TryParseModbusSpec(ds.Config, out var unit, out var start, out var count))
            return await ProbeModbusReadAsync(gw, unit, start, count, ct);

        if (string.Equals(gw.Type, "REST", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(ds.Config))
            return await ProbeRestGetAsync(gw, ds.Config!, ct);

        return await ProbeGatewayByTypeAsync(gw, ftpFullRoundTrip: false, timeoutMs: 3000, ct);
    }

    /// <summary>解析 Modbus 读取规格 "unit:start:count"（如 1:0:10）。</summary>
    private static bool TryParseModbusSpec(string? config, out byte unit, out ushort start, out ushort count)
    {
        unit = 1; start = 0; count = 1;
        if (string.IsNullOrWhiteSpace(config)) return false;
        var parts = config.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        return byte.TryParse(parts[0], out unit)
            && ushort.TryParse(parts[1], out start)
            && ushort.TryParse(parts[2], out count)
            && count is > 0 and <= 125;
    }

    private async Task<CsProbeResult> ProbeModbusReadAsync(CsGateway gw, byte unit, ushort start, ushort count, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(3000);
            await client.ConnectAsync(gw.Ip, gw.Port, timeoutCts.Token);
            await using var stream = client.GetStream();

            var txId = (ushort)Random.Shared.Next(1, 65000);
            var request = new byte[12];
            request[0] = (byte)(txId >> 8); request[1] = (byte)txId;
            request[2] = 0; request[3] = 0;
            request[4] = 0; request[5] = 6; // MBAP 长度 = unit + FC + addr(2) + qty(2)
            request[6] = unit; request[7] = 0x03;
            request[8] = (byte)(start >> 8); request[9] = (byte)start;
            request[10] = (byte)(count >> 8); request[11] = (byte)count;
            await stream.WriteAsync(request, timeoutCts.Token);

            var header = new byte[9];
            await ReadExactAsync(stream, header, timeoutCts.Token);
            var lengthField = (header[4] << 8) | header[5];
            var remaining = Math.Max(0, lengthField - 3);
            var data = new byte[remaining];
            if (remaining > 0) await ReadExactAsync(stream, data, timeoutCts.Token);
            sw.Stop();

            if ((header[7] & 0x80) != 0)
                return new CsProbeResult(false, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                    $"Modbus 异常码 0x{header[8]:X2}", Now());

            var firstValue = data.Length >= 2 ? (ushort)((data[0] << 8) | data[1]) : (ushort)0;
            return new CsProbeResult(true, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                $"Modbus FC03 读取 OK：寄存器[{start}]={firstValue}（共 {count}）", Now());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            return new CsProbeResult(false, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                $"Modbus 请求失败：{ex.Message}", Now());
        }
    }

    private async Task<CsProbeResult> ProbeRestGetAsync(CsGateway gw, string path, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var requestPath = path.Trim();
            if (!requestPath.StartsWith('/')) requestPath = "/" + requestPath;

            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(3000);
            await client.ConnectAsync(gw.Ip, gw.Port, timeoutCts.Token);
            await using var stream = client.GetStream();

            var request = $"GET {requestPath} HTTP/1.0\r\nHost: {gw.Ip}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(request), timeoutCts.Token);

            using var ms = new MemoryStream();
            var buffer = new byte[4096];
            int n;
            while ((n = await stream.ReadAsync(buffer, timeoutCts.Token)) > 0 && ms.Length < 8192)
                ms.Write(buffer, 0, n);
            sw.Stop();

            var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            var statusLine = text.Split("\r\n", 2)[0].Trim();
            var ok = statusLine.Contains(" 200") || statusLine.Contains(" 201") || statusLine.Contains(" 204");
            return new CsProbeResult(ok, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                $"HTTP {statusLine}", Now());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            return new CsProbeResult(false, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                $"HTTP 请求失败：{ex.Message}", Now());
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0) throw new IOException("连接被对端关闭");
            offset += read;
        }
    }

    /// <summary>数据源启用后的后台探测循环：按周期探测关联网关并回写最近结果。</summary>
    private async Task ProbeLoopAsync(CsDataSource ds, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_gateways.TryGetValue(ds.GatewayId, out var gw))
                {
                    var r = await ProbeDataSourceAsync(gw, ds, ct);
                    ds.LastUpdate = r.Timestamp;
                    ds.LastResult = r.Success ? $"OK {r.RttMs}ms · {r.Message}" : r.Message;
                    gw.Status = r.Success ? "运行中" : "停止";
                    if (r.Success) gw.LastHeartbeat = r.Timestamp;
                }
                else
                {
                    ds.LastUpdate = Now();
                    ds.LastResult = "关联网关不存在";
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "数据源 {Id} 探测异常", ds.Id);
            }

            try { await Task.Delay(Math.Max(1, ds.UpdateInterval) * 1000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
