using System.Diagnostics;
using System.Text;
using FluentFTP;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

/// <summary>CsConnectivityService FTP 客户端部分：基于 FluentFTP 的真实 FTP 验证。</summary>
public sealed partial class CsConnectivityService
{
    /// <summary>
    /// 真实 FTP 客户端验证：登录(USER/PASS) + 验 21 端口 + 列目录；
    /// fullRoundTrip=true 时再做上传/下载/删除 round-trip（验证上传下载能力）。
    /// </summary>
    public async Task<CsProbeResult> ProbeFtpClientAsync(CsGateway gw, bool fullRoundTrip, CancellationToken ct)
    {
        var port = gw.Port > 0 ? gw.Port : 21;
        var steps = new List<string>();
        var sw = Stopwatch.StartNew();
        var client = new AsyncFtpClient(gw.Ip, gw.Username ?? "", gw.Password ?? "", port);
        client.Config.ConnectTimeout = 5000;
        client.Config.ReadTimeout = 5000;
        client.Config.DataConnectionConnectTimeout = 5000;
        client.Config.DataConnectionReadTimeout = 5000;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(fullRoundTrip ? 20000 : 8000);
            var token = timeoutCts.Token;

            await client.Connect(token);
            steps.Add("登录成功(21端口可用)");

            var dir = string.IsNullOrWhiteSpace(gw.RemotePath) ? "/" : gw.RemotePath!.Trim();
            var listing = await client.GetListing(dir, token);
            steps.Add($"列目录{dir}={listing.Length}项");

            if (fullRoundTrip)
            {
                var prefix = dir == "/" ? "" : dir.TrimEnd('/');
                var name = $"{prefix}/__cs_probe_{Guid.NewGuid():N}.txt";
                var payload = Encoding.UTF8.GetBytes($"cs-ftp-probe {Now()}");
                var up = await client.UploadBytes(payload, name, FtpRemoteExists.Overwrite, true, token: token);
                if (up == FtpStatus.Success)
                {
                    steps.Add("上传成功");
                    var back = await client.DownloadBytes(name, token: token);
                    steps.Add(back != null && back.Length == payload.Length ? "下载校验一致" : "下载校验不符");
                    await client.DeleteFile(name, token);
                    steps.Add("清理完成");
                }
                else
                {
                    steps.Add($"上传失败({up})");
                }
            }

            sw.Stop();
            try { await client.Disconnect(token); } catch { /* ignore */ }
            return new CsProbeResult(true, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                "FTP " + string.Join("；", steps), Now());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var done = steps.Count > 0 ? $"（已完成：{string.Join("；", steps)}）" : "";
            return new CsProbeResult(false, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                $"FTP 失败：{ex.Message}{done}", Now());
        }
        finally
        {
            client.Dispose();
        }
    }
}
