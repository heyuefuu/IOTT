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

    /// <summary>数据源启用后的后台探测循环：按周期探测关联网关并回写最近结果。</summary>
    private async Task ProbeLoopAsync(CsDataSource ds, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_gateways.TryGetValue(ds.GatewayId, out var gw))
                {
                    var r = await ProbeGatewayByTypeAsync(gw, ftpFullRoundTrip: false, timeoutMs: 3000, ct);
                    ds.LastUpdate = r.Timestamp;
                    ds.LastResult = r.Success ? $"OK {r.RttMs}ms" : r.Message;
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
