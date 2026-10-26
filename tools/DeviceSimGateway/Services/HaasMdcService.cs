namespace DeviceSimGateway.Services;

using System.Net;
using System.Net.Sockets;
using System.Text;
using DeviceSimGateway.Core;

/// <summary>Haas MDC 模拟服务端（TCP 9999，ASCII）。对齐 Host 的 HaasMdcDriver/HaasCommandMap：
/// 连接以 Q100 探活；命名地址→Q 命令（Q100..Q700）、宏→Q600 NNNNN 读；写宏→E NNNNN value。
/// 响应：普通 `&gt;Qxxx VALUE`；宏 `&gt;Q600 MACRO, NNNNN, VALUE`。</summary>
public sealed class HaasMdcService : IDeviceService
{
    public string Key => "haas";
    public string Name => "Haas MDC";
    public string Description => "Haas MDC ASCII 服务端（TCP 9999，Q/E 命令）";
    public int DefaultPort => 9999;
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
        Message = $"监听 0.0.0.0:{port}（Haas MDC）";
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
            var reader = new StreamReader(stream, Encoding.ASCII);
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(); } catch { break; }
                if (line is null) break;
                line = line.Trim('\r', '\n', ' ');
                if (line.Length == 0) continue;
                var reply = Handle(line) + "\r\n";
                try { var b = Encoding.ASCII.GetBytes(reply); await stream.WriteAsync(b, ct); await stream.FlushAsync(ct); }
                catch { break; }
            }
        }
    }

    private string Handle(string cmd)
    {
        var s = _store!;
        // 写宏：E NNNNN value
        if (cmd.StartsWith('E') && cmd.Length > 1)
        {
            return cmd; // 回显即成功（IsWriteSuccessful: 无 ?/ERROR）
        }
        // 读宏：Q600 NNNNN
        if (cmd.StartsWith("Q600", StringComparison.OrdinalIgnoreCase))
        {
            var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int num = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 0;
            lock (s.Sync) return $"MACRO, {num}, {MacroValue(num).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }
        var code = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant() ?? cmd.ToUpperInvariant();
        lock (s.Sync)
        {
            return code switch
            {
                "Q100" => ">Q100 SIMHAAS-0001",
                "Q101" => ">Q101 100.19.000.0000",
                "Q102" => ">Q102 VF-2SS",
                "Q104" => ">Q104 IDLE",
                "Q200" => ">Q200 12",
                "Q201" => ">Q201 3",
                "Q300" => ">Q300 12345",
                "Q301" => ">Q301 678",
                "Q303" => ">Q303 42",
                "Q304" => ">Q304 40",
                "Q402" => $">Q402 {Reg(s.Holding, 3)}",
                "Q403" => $">Q403 {Reg(s.Holding, 4)}",
                "Q500" => $">Q500 RUNNING, PROGRAM O0001, PARTS {Reg(s.Holding, 3)}",
                "Q700" => ">Q700 100 (MOCK ALARM)",
                _ => ">? INVALID",
            };
        }
    }

    private double MacroValue(int num)
    {
        var s = _store!;
        return num switch
        {
            5021 => Reg(s.Holding, 0) / 1000.0,   // X
            5022 => Reg(s.Holding, 1) / 1000.0,   // Y
            5023 => Reg(s.Holding, 2) / 1000.0,   // Z
            3027 => Reg(s.Input, 0),              // spindle RPM
            1094 => Reg(s.Input, 2) % 100,        // spindle load %
            _ => Reg(s.Holding, Math.Clamp(num % 100, 0, s.Holding.Length - 1)),
        };
    }

    private static ushort Reg(ushort[] a, int i) => i >= 0 && i < a.Length ? a[i] : (ushort)0;
}
