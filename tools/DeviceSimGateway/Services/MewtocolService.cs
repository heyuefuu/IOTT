namespace DeviceSimGateway.Services;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DeviceSimGateway.Core;

/// <summary>Panasonic Mewtocol 模拟服务端（TCP ASCII）。
/// 请求：`%` + 站号(2) + [`#`] + 命令(3: RDD/RCC/WDD/WCS…) + 数据 + BCC(2) + CR；
/// 响应：`%` + 站号 + 命令 + 数据 + BCC(2) + CR（BCC = 自 `%` 起逐字节异或，2 位十六进制大写）。</summary>
public sealed class MewtocolService : IDeviceService
{
    public string Key => "mewtocol";
    public string Name => "Panasonic Mewtocol";
    public string Description => "Mewtocol ASCII 服务端（RDD/RCC/WDD…，BCC 校验）";
    public int DefaultPort => 5007;
    public bool Implemented => true;
    public ServiceState State { get; private set; } = ServiceState.Stopped;
    public string Message { get; private set; } = "";
    public int Port { get; private set; }

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private SimDataStore? _store;

    public Task StartAsync(int port, SimDataStore data, GeneratorConfig gen, CancellationToken ct)
    {
        _store = data; Port = port;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        State = ServiceState.Running;
        Message = $"监听 0.0.0.0:{port}（Mewtocol）";
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        if (_loop is not null) { try { await _loop.WaitAsync(TimeSpan.FromSeconds(3), ct); } catch { } }
        _listener = null; _cts = null; _loop = null;
        if (State == ServiceState.Running) { State = ServiceState.Stopped; Message = "已停止"; }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            _ = Task.Run(() => SessionAsync(client, ct), ct);
        }
    }

    private async Task SessionAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        using (client)
        {
            var stream = client.GetStream();
            var buf = new byte[1024];
            var sb = new StringBuilder();
            while (!ct.IsCancellationRequested)
            {
                int n;
                try { n = await stream.ReadAsync(buf, ct); } catch { break; }
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (b == 0x0D || b == 0x0A)
                    {
                        if (sb.Length == 0) continue; // 跳过 CRLF 的 LF
                        var line = sb.ToString(); sb.Clear();
                        if (line.Length < 4 || line[0] != '%') continue;
                        var reply = Handle(line);
                        if (reply is null) continue;
                        try { var rb = Encoding.ASCII.GetBytes(reply); await stream.WriteAsync(rb, ct); await stream.FlushAsync(ct); } catch { return; }
                    }
                    else sb.Append((char)b);
                }
            }
        }
    }

    private string? Handle(string frame)
    {
        var s = _store!;
        // % 站号(2) [ # ] 命令(3) 数据 BCC(2)
        var station = frame.Substring(1, 2);
        int p = 3;
        if (frame[p] == '#') p++;
        if (frame.Length < p + 3) return null;
        var cmd = frame.Substring(p, 3).ToUpperInvariant();
        p += 3;
        var payload = frame[p..];
        // 去掉尾部 BCC(2)
        var data = payload.Length >= 2 ? payload[..^2] : payload;

        lock (s.Sync)
        {
            switch (cmd)
            {
                case "RDD":
                case "RDS":
                {
                    var (addr, count) = ParseAddrCount(data);
                    var sb = new StringBuilder();
                    for (int i = 0; i < count; i++)
                        sb.Append(Reg(s.Holding, addr + i).ToString("X4", CultureInfo.InvariantCulture));
                    return Response(station, cmd, sb.ToString());
                }
                case "RCC":
                case "RCP":
                {
                    var (addr, count) = ParseAddrCount(data);
                    var sb = new StringBuilder();
                    for (int i = 0; i < count; i++) sb.Append(Bit(s, addr + i) ? '1' : '0');
                    return Response(station, cmd, sb.ToString());
                }
                case "WDD":
                case "WDS":
                {
                    // 请求：地址(5) + 数据(每字 4 hex)；回显即成功
                    var d = data.Length >= 5 ? data[5..] : "";
                    var ok = d.Length > 0;
                    return Response(station, cmd, ok ? "0" : "1");
                }
                case "WCC":
                case "WCS":
                    return Response(station, cmd, "0");
                case "RDS_ERR":
                default:
                    return Response(station, cmd, "0");
            }
        }
    }

    private (int Addr, int Count) ParseAddrCount(string data)
    {
        int addr = 0, count = 1;
        if (data.Length >= 5) int.TryParse(data[..5], NumberStyles.Integer, CultureInfo.InvariantCulture, out addr);
        if (data.Length >= 9) { if (int.TryParse(data[5..9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c > 0) count = Math.Clamp(c, 1, 128); }
        return (addr, count);
    }

    private static bool Bit(SimDataStore s, int index)
    {
        int w = index / 16, b = index % 16;
        return w < s.Coils.Length ? s.Coils[w] : false;
    }

    private static string Response(string station, string cmd, string data)
    {
        // HSL 的 Mewtocol 客户端把命令当 2 字符（如 RDD→RD、RCC→RC），响应回 2 字符命令。
        var c = cmd.Length >= 2 ? cmd[..2] : cmd;
        var body = "%" + station + c + data;
        var bcc = Bcc(body);
        return body + bcc + "\r";
    }

    /// <summary>BCC：自 `%` 起逐字节异或，2 位十六进制大写。</summary>
    internal static string Bcc(string text)
    {
        byte x = 0;
        foreach (var ch in text) x ^= (byte)ch;
        return x.ToString("X2", CultureInfo.InvariantCulture);
    }

    private static ushort Reg(ushort[] a, int i) => i >= 0 && i < a.Length ? a[i] : (ushort)0;
}
