namespace DeviceSimGateway.Services;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DeviceSimGateway.Core;

/// <summary>FTP 服务端（纯文本 FTP），模拟数控程序库：支持 LIST/NLST/MLSD/RETR/STOR/DELE/SIZE/MKD/RMD 等，
/// 供 Host 的 FtpTransferDriver（FluentFTP）做程序列举/上传/下载/删除。</summary>
public sealed class FtpServerService : IDeviceService
{
    public string Key => "ftp";
    public string Name => "FTP Server";
    public string Description => "FTP 服务端（模拟数控程序库：列举/上传/下载/删除）";
    public int DefaultPort => 21;
    public bool Implemented => true;
    public ServiceState State { get; private set; } = ServiceState.Stopped;
    public string Message { get; private set; } = "";
    public int Port { get; private set; }

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private SimDataStore? _store;
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _dirs = new(StringComparer.OrdinalIgnoreCase);

    public Task StartAsync(int port, SimDataStore data, GeneratorConfig gen, CancellationToken ct)
    {
        Port = port;
        _store = data;
        Seed();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        State = ServiceState.Running;
        Message = $"监听 0.0.0.0:{port}（程序库 {_files.Count} 个文件）";
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

    private void Seed()
    {
        _files["/O0001.CNC"] = Encoding.ASCII.GetBytes("(MOCK) G21 G90\nM03 S1200\nG0 X0 Z0\nM30\n");
        _files["/O0002.CNC"] = Encoding.ASCII.GetBytes("(MOCK) G21 G90\nM03 S800\nG1 X10 Z-5\nM30\n");
        _dirs["/PROGRAMS"] = true;
        _files["/PROGRAMS/O0003.CNC"] = Encoding.ASCII.GetBytes("(MOCK submode)\nM30\n");
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

    private async Task SessionAsync(TcpClient control, CancellationToken ct)
    {
        control.NoDelay = true;
        using (control)
        {
            var cs = control.GetStream();
            var reader = new StreamReader(cs, Encoding.ASCII);
            var writer = new StreamWriter(cs, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
            string cwd = "/";
            TcpListener? passive = null;

            async Task Reply(string line) => await writer.WriteLineAsync(line);
            await Reply("220 DeviceSimGateway FTP ready");

            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(); } catch { break; }
                if (line is null) break;
                var sp = line.IndexOf(' ');
                var cmd = (sp < 0 ? line : line[..sp]).ToUpperInvariant();
                var arg = sp < 0 ? "" : line[(sp + 1)..];
                try
                {
                    // 随机失败：仅作用于数据类命令，避免把登录/参数命令也打挂。
                    if (cmd is "RETR" or "STOR" or "LIST" or "NLST" or "MLSD" or "SIZE" && _store?.Failure.ShouldFail() == true)
                    {
                        if (_store!.Failure.DropConnection) return;   // 随机失败：断链
                        await Reply("451 Requested action aborted: local error");
                        continue;
                    }
                    switch (cmd)
                    {
                        case "USER": await Reply("331 Need password"); break;
                        case "PASS": await Reply("230 Logged in"); break;
                        case "SYST": await Reply("215 UNIX Type: L8"); break;
                        case "FEAT": await Reply("211-Features:"); await Reply(" SIZE"); await Reply(" UTF8"); await Reply(" MLST type*;size*;modify*;"); await Reply("211 End"); break;
                        case "OPTS": await Reply("200 OK"); break;
                        case "TYPE": await Reply("200 Type set"); break;
                        case "PWD": await Reply("257 \"" + cwd + "\" is current directory"); break;
                        case "CWD":
                            { var d = Norm(arg, cwd); if (d == "/" || _dirs.ContainsKey(d)) { cwd = d; await Reply("250 OK"); } else await Reply("550 No such directory"); break; }
                        case "NOOP": await Reply("200 OK"); break;
                        case "PASV":
                            passive?.Stop();
                            passive = new TcpListener(((IPEndPoint)control.Client.LocalEndPoint!).Address, 0);
                            passive.Start();
                            { var p = ((IPEndPoint)passive.LocalEndpoint).Port; var ip = ((IPEndPoint)control.Client.LocalEndPoint!).Address.GetAddressBytes(); await Reply($"227 Entering Passive Mode ({ip[0]},{ip[1]},{ip[2]},{ip[3]},{p / 256},{p % 256})"); }
                            break;
                        case "EPSV":
                            passive?.Stop();
                            passive = new TcpListener(((IPEndPoint)control.Client.LocalEndPoint!).Address, 0);
                            passive.Start();
                            { var p = ((IPEndPoint)passive.LocalEndpoint).Port; await Reply($"229 Entering Extended Passive Mode (|||{p}|)"); }
                            break;
                        case "LIST":
                        case "NLST":
                        case "MLSD":
                            await TransferList(cmd, arg, cwd, passive, Reply);
                            passive = null; break;
                        case "SIZE":
                            { var path = Norm(arg, cwd); if (_files.TryGetValue(path, out var b)) await Reply($"213 {b.Length}"); else await Reply("550 Not found"); break; }
                        case "RETR":
                            { var path = Norm(arg, cwd); if (!_files.TryGetValue(path, out var b)) { await Reply("550 Not found"); break; } await using var d = await AcceptData(passive); passive = null; if (d is null) { await Reply("425 No data connection"); break; } await Reply("150 Opening data"); await d.WriteAsync(b, ct); await d.FlushAsync(ct); await Reply("226 Transfer complete"); break; }
                        case "STOR":
                            { var path = Norm(arg, cwd); await using var d = await AcceptData(passive); passive = null; if (d is null) { await Reply("425 No data connection"); break; } await Reply("150 Ok to send"); using var ms = new MemoryStream(); await d.CopyToAsync(ms, ct); _files[path] = ms.ToArray(); await Reply("226 Transfer complete"); break; }
                        case "DELE":
                            { var path = Norm(arg, cwd); await Reply(_files.TryRemove(path, out _) ? "250 Deleted" : "550 Not found"); break; }
                        case "MKD":
                            { var path = Norm(arg, cwd); _dirs[path] = true; await Reply("257 \"" + path + "\" created"); break; }
                        case "RMD":
                            { var path = Norm(arg, cwd); await Reply(_dirs.TryRemove(path, out _) ? "250 Removed" : "550 Not found"); break; }
                        case "QUIT": await Reply("221 Bye"); return;
                        default: await Reply("502 Not implemented"); break;
                    }
                }
                catch (Exception ex) { try { await Reply("550 " + ex.Message); } catch { } }
            }
        }
    }

    private async Task TransferList(string cmd, string arg, string cwd, TcpListener? passive, Func<string, Task> reply)
    {
        var dir = Norm(string.IsNullOrWhiteSpace(arg) ? cwd : arg, cwd);
        if (dir != "/") dir = dir.TrimEnd('/');
        var entries = ListEntries(dir);
        await using var d = await AcceptData(passive);
        if (d is null) { await reply("425 No data connection"); return; }
        await reply("150 Here comes the directory listing");
        foreach (var (name, isDir, size) in entries)
        {
            var text = cmd switch
            {
                "MLSD" => $"type={(isDir ? "dir" : "file")};size={size};modify=20260101000000; {name}\r\n",
                "NLST" => name + "\r\n",
                _ => (isDir ? "drwxr-xr-x" : "-rw-r--r--") + $" 1 owner group {size,10} Jan 01 00:00 {name}\r\n",
            };
            var bytes = Encoding.ASCII.GetBytes(text);
            await d.WriteAsync(bytes);
        }
        await d.FlushAsync();
        await reply("226 Directory send OK");
    }

    private List<(string Name, bool IsDir, long Size)> ListEntries(string dir)
    {
        var prefix = dir == "/" ? "/" : dir + "/";
        var res = new List<(string, bool, long)>();
        foreach (var d in _dirs.Keys)
            if (ParentOf(d) == (dir == "/" ? "/" : dir) && d != dir) res.Add((FileName(d), true, 0));
        foreach (var kv in _files)
            if (ParentOf(kv.Key) == (dir == "/" ? "/" : dir)) res.Add((FileName(kv.Key), false, kv.Value.Length));
        return res;
    }

    private static string ParentOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static async Task<NetworkStream?> AcceptData(TcpListener? passive)
    {
        if (passive is null) return null;
        var c = await passive.AcceptTcpClientAsync();
        passive.Stop();
        return c.GetStream();
    }

    private static string Norm(string arg, string cwd)
    {
        if (string.IsNullOrWhiteSpace(arg)) return cwd;
        var p = arg.Replace('\\', '/').Trim();
        if (!p.StartsWith('/')) p = (cwd == "/" ? "/" : cwd + "/") + p;
        var parts = new List<string>();
        foreach (var seg in p.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return "/" + string.Join('/', parts);
    }
}
