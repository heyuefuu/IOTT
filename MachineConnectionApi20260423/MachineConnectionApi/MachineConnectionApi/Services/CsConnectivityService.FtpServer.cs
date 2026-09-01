using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

/// <summary>CsConnectivityService 服务端 FTP 部分：手写最小 FTP server（零依赖，被动模式）。</summary>
public sealed partial class CsConnectivityService
{
    private async Task FtpAcceptLoopAsync(CsServerService svc, ServerRuntime runtime)
    {
        var ct = runtime.Cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await runtime.Listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "FTP 服务端 {Id} 接受异常", svc.Id); break; }

            if (!runtime.ConnectionSlots.Wait(0)) { client.Close(); continue; }
            _ = HandleFtpControlAsync(svc, runtime, client, ct);
        }
    }

    private async Task HandleFtpControlAsync(CsServerService svc, ServerRuntime runtime, TcpClient control, CancellationToken ct)
    {
        var connId = Guid.NewGuid().ToString("N");
        var remote = control.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var connectedAt = Now();
        runtime.Connections[connId] = new CsServerConnection(remote, connectedAt, 0);
        svc.ClientCount = runtime.Connections.Count;
        svc.LastAccess = connectedAt;

        var localIp = (control.Client.LocalEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
        var controlRemoteIp = (control.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
        TcpListener? data = null;        // 被动模式（PASV）监听器
        IPEndPoint? activePort = null;   // 主动模式（PORT）客户端数据端点
        var authed = false;
        var user = "";
        long bytes = 0;

        bool HasDataChannel() => data is not null || activePort is not null;
        void ResetDataChannel() { data?.Stop(); data = null; activePort = null; }

        try
        {
            using (control)
            await using (var stream = control.GetStream())
            {
                var reader = new StreamReader(stream, Encoding.ASCII);

                async Task Send(string s)
                {
                    var b = Encoding.ASCII.GetBytes(s + "\r\n");
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(FtpControlIdleTimeout);
                    await stream.WriteAsync(b, timeoutCts.Token);
                }

                await Send("220 CS-FTP service ready");
                var quit = false;
                while (!quit && !ct.IsCancellationRequested)
                {
                    string? line;
                    try { line = await ReadFtpCommandAsync(reader, ct); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await Send("421 Control connection timed out");
                        break;
                    }
                    if (line == null) break;
                    var sp = line.IndexOf(' ');
                    var cmd = (sp < 0 ? line : line[..sp]).ToUpperInvariant();
                    var arg = sp < 0 ? "" : line[(sp + 1)..].Trim();

                    if (!authed && cmd is "PASV" or "PORT" or "LIST" or "NLST" or
                        "STOR" or "RETR" or "DELE" or "SIZE")
                    {
                        await Send("530 Not logged in");
                        continue;
                    }

                    // PASV 接受连入需校验来源 IP 与控制连接一致；PORT 主动连回客户端申报端点。
                    async Task<TcpClient> OpenDataAsync()
                    {
                        if (activePort is { } ep)
                            return await ConnectFtpActiveDataAsync(ep, ct);
                        return await AcceptFtpDataClientAsync(data!, controlRemoteIp, ct);
                    }

                    switch (cmd)
                    {
                        case "USER":
                            user = arg;
                            authed = false;
                            await Send("331 Password required");
                            break;
                        case "PASS":
                            authed = CheckFtpAuth(svc, user, arg);
                            if (!authed)
                            {
                                // 固定延时拉低口令爆破速率（每连接失败即断开）
                                await Task.Delay(FtpAuthFailureDelay, ct);
                                await Send("530 Login incorrect");
                                quit = true;
                                break;
                            }
                            await Send("230 Login successful");
                            break;
                        case "SYST": await Send("215 UNIX Type: L8"); break;
                        case "FEAT": await Send("211-Features\r\n PASV\r\n SIZE\r\n211 End"); break;
                        case "OPTS": await Send("200 OK"); break;
                        case "TYPE":
                            // 存储始终按二进制；ASCII 模式不做换行转换，如实告知客户端
                            await Send(arg.StartsWith('I') ? "200 Type set to I" : "200 Type set (binary only)");
                            break;
                        case "PWD": case "XPWD": await Send("257 \"/\" is current directory"); break;
                        case "CWD": case "CDUP": await Send("250 OK"); break;
                        case "NOOP": await Send("200 OK"); break;
                        case "SIZE":
                            await Send(runtime.FtpFiles.TryGetValue(NormalizeFtpName(arg), out var sized)
                                ? $"213 {sized.LongLength}" : "550 File not found");
                            break;
                        case "PORT":
                            ResetDataChannel();
                            if (TryParseFtpPortArgument(arg, out var portEp) &&
                                portEp.Address.Equals(controlRemoteIp.MapToIPv4()))
                            {
                                activePort = portEp;
                                await Send("200 PORT command successful");
                            }
                            else
                            {
                                // 拒绝与控制连接不同源的数据端点，阻断 FTP bounce
                                await Send("501 Invalid PORT argument");
                            }
                            break;
                        case "PASV":
                            ResetDataChannel();
                            data = new TcpListener(IPAddress.Any, 0);
                            data.Start();
                            var dp = ((IPEndPoint)data.LocalEndpoint).Port;
                            var ip = localIp.MapToIPv4().GetAddressBytes();
                            await Send($"227 Entering Passive Mode ({ip[0]},{ip[1]},{ip[2]},{ip[3]},{dp / 256},{dp % 256})");
                            break;
                        case "LIST": case "NLST":
                            if (!HasDataChannel()) { await Send("425 Use PASV or PORT first"); break; }
                            await Send("150 Opening data connection");
                            try
                            {
                                using (var dc = await OpenDataAsync())
                                await using (var ds = dc.GetStream())
                                {
                                    var sb = new StringBuilder();
                                    foreach (var kv in runtime.FtpFiles)
                                        sb.Append(cmd == "NLST"
                                            ? $"{kv.Key}\r\n"
                                            : $"-rw-r--r-- 1 cs cs {kv.Value.Length} Jan 01 00:00 {kv.Key}\r\n");
                                    var lb = Encoding.ASCII.GetBytes(sb.ToString());
                                    await WriteFtpDataAsync(ds, lb, ct);
                                }
                                await Send("226 Transfer complete");
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                await Send("426 Data connection timed out");
                            }
                            finally { ResetDataChannel(); }
                            break;
                        case "STOR":
                            if (!HasDataChannel()) { await Send("425 Use PASV or PORT first"); break; }
                            if (!await _ftpUploadSlots.WaitAsync(0, ct))
                            {
                                ResetDataChannel();
                                await Send("450 Too many concurrent uploads");
                                break;
                            }
                            try
                            {
                                await Send("150 Ready to receive");
                                byte[]? uploaded;
                                using (var dc = await OpenDataAsync())
                                await using (var ds = dc.GetStream())
                                    uploaded = await ReadFtpUploadAsync(ds, ct);
                                if (uploaded == null)
                                {
                                    await Send("552 File exceeds server size limit");
                                    break;
                                }
                                if (!TryStoreFtpFile(runtime, NormalizeFtpName(arg), uploaded,
                                        out var storeError))
                                {
                                    await Send(storeError);
                                    break;
                                }
                                bytes += uploaded.LongLength;
                                runtime.Connections[connId] = new CsServerConnection(remote, connectedAt, bytes);
                                await Send("226 Transfer complete");
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                await Send("426 Data connection timed out");
                            }
                            finally
                            {
                                ResetDataChannel();
                                _ftpUploadSlots.Release();
                            }
                            break;
                        case "RETR":
                            if (!HasDataChannel()) { await Send("425 Use PASV or PORT first"); break; }
                            if (!runtime.FtpFiles.TryGetValue(NormalizeFtpName(arg), out var fd))
                            { await Send("550 File not found"); break; }
                            await Send("150 Opening data connection");
                            try
                            {
                                using (var dc = await OpenDataAsync())
                                await using (var ds = dc.GetStream())
                                {
                                    await WriteFtpDataAsync(ds, fd, ct);
                                }
                                await Send("226 Transfer complete");
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                await Send("426 Data connection timed out");
                            }
                            finally { ResetDataChannel(); }
                            break;
                        case "DELE":
                            await Send(RemoveFtpFile(runtime, NormalizeFtpName(arg))
                                ? "250 Deleted" : "550 File not found");
                            break;
                        case "QUIT": await Send("221 Goodbye"); quit = true; break;
                        default: await Send("502 Command not implemented"); break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "FTP 控制连接 {Remote} 结束", remote); }
        finally
        {
            data?.Stop();
            runtime.Connections.TryRemove(connId, out _);
            svc.ClientCount = runtime.Connections.Count;
            runtime.ConnectionSlots.Release();
        }
    }

    private static async Task<string?> ReadFtpCommandAsync(
        StreamReader reader, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(FtpControlIdleTimeout);
        return await reader.ReadLineAsync(timeoutCts.Token);
    }

    /// <summary>接受 PASV 数据连接；来源 IP 必须与控制连接一致，防止第三方抢连数据端口。</summary>
    private static async Task<TcpClient> AcceptFtpDataClientAsync(
        TcpListener listener, IPAddress expectedRemoteIp, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(FtpDataTransferTimeout);
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync(timeoutCts.Token);
            var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (remoteIp is not null &&
                remoteIp.MapToIPv4().Equals(expectedRemoteIp.MapToIPv4()))
                return client;
            client.Close();
        }
    }

    /// <summary>主动模式：连回客户端 PORT 申报的数据端点。</summary>
    private static async Task<TcpClient> ConnectFtpActiveDataAsync(
        IPEndPoint endpoint, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(FtpDataTransferTimeout);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(endpoint.Address, endpoint.Port, timeoutCts.Token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>解析 PORT h1,h2,h3,h4,p1,p2 参数。</summary>
    private static bool TryParseFtpPortArgument(string arg, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.None, 0);
        var parts = arg.Split(',');
        if (parts.Length != 6) return false;
        var values = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(parts[i].Trim(), out values[i])) return false;
        }
        var port = values[4] * 256 + values[5];
        if (port is <= 0 or > 65535) return false;
        endpoint = new IPEndPoint(new IPAddress(values[..4]), port);
        return true;
    }

    private static async Task WriteFtpDataAsync(
        Stream stream, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(FtpDataTransferTimeout);
        await stream.WriteAsync(content, timeoutCts.Token);
    }

    private static bool CheckFtpAuth(CsServerService svc, string user, string pass)
    {
        return !string.IsNullOrWhiteSpace(svc.Username)
            && !string.IsNullOrEmpty(svc.Password)
            && string.Equals(user, svc.Username, StringComparison.Ordinal)
            && string.Equals(pass, svc.Password, StringComparison.Ordinal);
    }

    private static async Task<byte[]?> ReadFtpUploadAsync(
        Stream stream, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(FtpDataTransferTimeout);
        var timeoutToken = timeoutCts.Token;
        using var memory = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, timeoutToken);
            if (read == 0) return memory.ToArray();
            if (memory.Length + read > MaxFtpFileBytes) return null;
            await memory.WriteAsync(buffer.AsMemory(0, read), timeoutToken);
        }
    }

    private bool TryStoreFtpFile(
        ServerRuntime runtime, string name, byte[] content, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "553 Invalid file name";
            return false;
        }
        lock (_ftpStorageGate)
        {
            if (runtime.Cts.IsCancellationRequested)
            {
                error = "450 Service stopping";
                return false;
            }
            var exists = runtime.FtpFiles.TryGetValue(name, out var previous);
            if (!exists && runtime.FtpFiles.Count >= MaxFtpFileCountPerServer)
            {
                error = "552 File count limit exceeded";
                return false;
            }
            var projected = _ftpStoredBytes - (previous?.LongLength ?? 0)
                + content.LongLength;
            if (projected > MaxFtpStoredBytes)
            {
                error = "552 Server storage limit exceeded";
                return false;
            }
            runtime.FtpFiles[name] = content;
            _ftpStoredBytes = projected;
            error = "";
            return true;
        }
    }

    private bool RemoveFtpFile(ServerRuntime runtime, string name)
    {
        lock (_ftpStorageGate)
        {
            if (!runtime.FtpFiles.TryRemove(name, out var removed)) return false;
            _ftpStoredBytes = Math.Max(0, _ftpStoredBytes - removed.LongLength);
            return true;
        }
    }

    private static string NormalizeFtpName(string path)
    {
        var p = path.Replace('\\', '/').Trim();
        var idx = p.LastIndexOf('/');
        return idx >= 0 ? p[(idx + 1)..] : p;
    }
}
