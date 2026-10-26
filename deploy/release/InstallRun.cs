// 单文件脚本（.NET 10 file-based app）：
//   在目标机上配置端口/数据库、启动/停止/检测 Host 与 Gateway（可选前端），并做异常处理与回滚。
// 运行（在打包目录内，或指定 --root）：
//   dotnet run .\InstallRun.cs -- --action install --db-password 你的密码
//   dotnet run .\InstallRun.cs -- --action status
//   dotnet run .\InstallRun.cs -- --action stop
//   dotnet run .\InstallRun.cs -- --action start --disable-influx --disable-mqtt
//   dotnet run .\InstallRun.cs -- --action install --check-only
// 说明：包内脚本与后端以“框架依赖”发布时，目标机需 .NET 8 ASP.NET Core 运行时；
//       若打包时用了 --runtime（自包含），则无需安装 .NET 运行时。

using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

Console.OutputEncoding = Encoding.UTF8;
return InstallRun.Run(args);

static class InstallRun
{
    static readonly UTF8Encoding Utf8NoBom = new(false);
    static string _logFile = "";

    public static int Run(string[] args)
    {
        if (Has(args, "--help") || Has(args, "-h")) { Help(); return 0; }

        string root;
        try { root = ResolveRoot(ValueAfter(args, "--root")); }
        catch (Exception ex) { Console.Error.WriteLine("[错误] " + ex.Message); return 2; }

        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(Path.Combine(root, "run"));
        _logFile = Path.Combine(root, "logs", $"install-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var action = (ValueAfter(args, "--action") ?? "install").ToLowerInvariant();
        if (Has(args, "--check-only")) action = "check";

        var opt = new Options
        {
            Root = root,
            HostPort = IntAfter(args, "--host-port", 5173),
            GatewayPort = IntAfter(args, "--gateway-port", 5087),
            FrontendPort = IntAfter(args, "--frontend-port", 4171),
            MySqlPort = IntAfter(args, "--mysql-port", 3307),
            InfluxPort = IntAfter(args, "--influx-port", 8181),
            DbHost = ValueAfter(args, "--db-host") ?? "127.0.0.1",
            DbUser = ValueAfter(args, "--db-user") ?? "iott",
            DbPassword = ValueAfter(args, "--db-password") ?? "",
            SkipDb = Has(args, "--skip-db"),
            SkipFrontend = Has(args, "--skip-frontend"),
            DisableInflux = Has(args, "--disable-influx"),
            DisableMqtt = Has(args, "--disable-mqtt"),
            Force = Has(args, "--force"),
            ProvisionMySql = Has(args, "--provision-mysql"),
        };

        Log($"仓库/包目录：{opt.Root}");
        Log($"操作：{action}    端口 Host={opt.HostPort} Gateway={opt.GatewayPort} Frontend={opt.FrontendPort} MySQL={opt.MySqlPort}");

        try
        {
            switch (action)
            {
                case "install":
                    Preflight(opt);
                    Configure(opt);
                    Start(opt);
                    return Report(opt) ? 0 : 1;
                case "start":
                    Preflight(opt);
                    Start(opt);
                    return Report(opt) ? 0 : 1;
                case "stop":
                    Stop(opt);
                    return 0;
                case "restart":
                    Stop(opt); Preflight(opt); Start(opt);
                    return Report(opt) ? 0 : 1;
                case "status":
                case "check":
                    return Report(opt) ? 0 : 1;
                default:
                    Console.Error.WriteLine($"[错误] 未知 --action：{action}（install|start|stop|restart|status|check）");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Log("[失败] " + ex.Message);
            TryRollbackLocalJson(opt);
            return 1;
        }
    }

    // ---------- 预检 ----------
    static void Preflight(Options o)
    {
        Log("---- 预检 ----");
        var selfHost = File.Exists(Path.Combine(o.Root, "host", "IndustrialIoT.Host.exe"));
        var selfGw = File.Exists(Path.Combine(o.Root, "gateway", "MachineConnectionApi.exe"));
        var frameworkDependent = !(selfHost && selfGw);

        if (frameworkDependent)
        {
            if (!CommandOk("dotnet", "--list-runtimes", out var runtimes) || !runtimes.Contains("Microsoft.AspNetCore.App 8."))
                throw new InvalidOperationException("未检测到 .NET 8 ASP.NET Core 运行时。请安装 ASP.NET Core Runtime 8，或用 --runtime 打包自包含版本。");
            Log("  .NET 运行时：OK");
        }
        else Log("  自包含发布：无需目标机 .NET 运行时");

        if (!o.SkipFrontend)
        {
            if (!CommandOk("node", "--version", out var nodeVer)) throw new InvalidOperationException("未检测到 Node.js（托管前端需要）。可用 --skip-frontend 跳过。");
            Log($"  Node.js：{nodeVer.Trim()} OK");
        }

        CheckPort("Host", o.HostPort, o.Force);
        CheckPort("Gateway", o.GatewayPort, o.Force);
        if (!o.SkipFrontend) CheckPort("Frontend", o.FrontendPort, o.Force);
        Log($"  （MySQL {o.MySqlPort} / InfluxDB {o.InfluxPort} 若为外部服务请自行保证可达）");
    }

    static void CheckPort(string name, int port, bool force)
    {
        if (!PortOpen("127.0.0.1", port)) { Log($"  端口 {port}（{name}）空闲"); return; }
        if (!force) throw new InvalidOperationException($"端口 {port}（{name}）已被占用；使用 --force 结束占用进程，或改用 --{name.ToLowerInvariant()}-port 指定其它端口。");
        Log($"  端口 {port}（{name}）被占用，--force：结束占用进程");
        KillPortOwner(port);
        Thread.Sleep(800);
        if (PortOpen("127.0.0.1", port)) throw new InvalidOperationException($"端口 {port}（{name}）仍被占用。");
    }

    // ---------- 配置 ----------
    static void Configure(Options o)
    {
        Log("---- 写入 appsettings.Local.json ----");
        if (o.ProvisionMySql)
        {
            var mysqlScript = Path.Combine(o.Root, "deploy", "mysql", "Start-MySql.ps1");
            if (File.Exists(mysqlScript))
            {
                Log("  调用 deploy/mysql/Start-MySql.ps1 预置本机 MySQL");
                var code = RunInherited("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{mysqlScript}\"", o.Root);
                if (code != 0) throw new InvalidOperationException("Start-MySql.ps1 失败");
            }
            else Log("  [跳过] 未找到 deploy/mysql/Start-MySql.ps1（--provision-mysql）");
        }

        var dbConn = $"Server={o.DbHost};Port={o.MySqlPort};Database={{0}};User ID={o.DbUser};" +
                     (string.IsNullOrEmpty(o.DbPassword) ? "" : $"Password={o.DbPassword};") + "DateTimeKind=Utc;";

        var hostLocal = new JsonObject { ["Urls"] = $"http://0.0.0.0:{o.HostPort}" };
        if (!o.SkipDb)
        {
            hostLocal["ConnectionStrings"] = new JsonObject { ["Default"] = string.Format(dbConn, "IndustrialIoT") };
            hostLocal["Database"] = new JsonObject { ["ServerVersion"] = "8.4.0" };
        }
        WriteLocal(Path.Combine(o.Root, "host", "appsettings.Local.json"), hostLocal);

        var gwLocal = new JsonObject
        {
            ["Urls"] = $"http://0.0.0.0:{o.GatewayPort}",
            ["IndustrialIoT"] = new JsonObject { ["BaseUrl"] = $"http://localhost:{o.HostPort}" },
            ["Verify"] = new JsonObject { ["Simulate"] = false },
        };
        if (!o.SkipDb)
        {
            gwLocal["ConnectionStrings"] = new JsonObject { ["MachineCollection"] = string.Format(dbConn, "MachineCollection") };
            gwLocal["Database"] = new JsonObject { ["ServerVersion"] = "8.4.0" };
        }
        if (o.DisableInflux) gwLocal["InfluxDB"] = new JsonObject { ["Enabled"] = false };
        if (o.DisableMqtt) gwLocal["Mqtt"] = new JsonObject { ["Enabled"] = false };
        WriteLocal(Path.Combine(o.Root, "gateway", "appsettings.Local.json"), gwLocal);
    }

    static void WriteLocal(string path, JsonObject content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) { File.Copy(path, path + ".bak", true); _writtenBackups.Add(path + ".bak"); }
        File.WriteAllText(path, content.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);
        Log("  已写入 " + path);
    }

    static readonly List<string> _writtenBackups = new();
    static void TryRollbackLocalJson(Options o)
    {
        foreach (var bak in _writtenBackups)
        {
            try { if (File.Exists(bak)) { File.Copy(bak, bak[..^4], true); Log("  已回滚 " + bak[..^4]); } } catch { }
        }
    }

    // ---------- 启动/停止 ----------
    static void Start(Options o)
    {
        Log("---- 启动服务 ----");
        StartHost(o);
        StartGateway(o);
        if (!o.SkipFrontend) StartFrontend(o);
        else Log("  [跳过] 前端（--skip-frontend）");
        Log("  等待服务就绪…");
        WaitHttp($"http://localhost:{o.HostPort}/api/Devices", 60);
        WaitHttp($"http://localhost:{o.GatewayPort}/api/system/status", 60);
    }

    static void StartHost(Options o)
    {
        var dir = Path.Combine(o.Root, "host");
        var (exe, args) = ResolveApp(dir, "IndustrialIoT.Host", $"http://0.0.0.0:{o.HostPort}");
        StartDetached(exe, args, dir, Path.Combine(o.Root, "logs", "host"), Path.Combine(o.Root, "run", "host.pid"), null);
        Log($"  Host 启动（{o.HostPort}）");
    }

    static void StartGateway(Options o)
    {
        var dir = Path.Combine(o.Root, "gateway");
        var (exe, args) = ResolveApp(dir, "MachineConnectionApi", $"http://0.0.0.0:{o.GatewayPort}");
        StartDetached(exe, args, dir, Path.Combine(o.Root, "logs", "gateway"), Path.Combine(o.Root, "run", "gateway.pid"), null);
        Log($"  Gateway 启动（{o.GatewayPort}）");
    }

    static void StartFrontend(Options o)
    {
        var dir = Path.Combine(o.Root, "frontend");
        var serve = Path.Combine(dir, "serve.mjs");
        if (!File.Exists(serve)) { Log("  [跳过] 未找到 frontend/serve.mjs"); return; }
        var env = new Dictionary<string, string>
        {
            ["FRONTEND_PORT"] = o.FrontendPort.ToString(),
            ["GATEWAY_TARGET"] = $"http://localhost:{o.GatewayPort}",
            ["HOST_TARGET"] = $"http://localhost:{o.HostPort}",
        };
        StartDetached("node", $"\"{serve}\"", dir, Path.Combine(o.Root, "logs", "frontend"), Path.Combine(o.Root, "run", "frontend.pid"), env);
        Log($"  前端启动（{o.FrontendPort}）");
    }

    static (string Exe, string Args) ResolveApp(string dir, string dllBase, string urls)
    {
        var exe = Path.Combine(dir, dllBase + ".exe");
        if (File.Exists(exe)) return (exe, $"--urls {urls} --environment Production");
        var dll = Path.Combine(dir, dllBase + ".dll");
        if (!File.Exists(dll)) throw new FileNotFoundException($"未找到 {dll}（或自包含 exe）");
        return ("dotnet", $"\"{dll}\" --urls {urls} --environment Production");
    }

    static void Stop(Options o)
    {
        Log("---- 停止服务 ----");
        StopByPidFile(Path.Combine(o.Root, "run", "frontend.pid"), "前端");
        StopByPidFile(Path.Combine(o.Root, "run", "gateway.pid"), "Gateway");
        StopByPidFile(Path.Combine(o.Root, "run", "host.pid"), "Host");
        // 兜底：按端口结束占用进程
        foreach (var (name, port) in new[] { ("Host", o.HostPort), ("Gateway", o.GatewayPort), ("Frontend", o.FrontendPort) })
            if (PortOpen("127.0.0.1", port)) { Log($"  {name} 仍在监听 {port}，按端口结束"); KillPortOwner(port); }
        Log("  已停止");
    }

    static void StopByPidFile(string pidFile, string name)
    {
        if (!File.Exists(pidFile)) return;
        try
        {
            var pid = int.Parse(File.ReadAllText(pidFile).Trim());
            var p = Process.GetProcessById(pid);
            Log($"  停止 {name}（PID {pid}）");
            RunInherited("taskkill", $"/PID {pid} /T /F", Directory.GetCurrentDirectory());
        }
        catch { /* 进程不存在 */ }
        try { File.Delete(pidFile); } catch { }
    }

    // ---------- 状态/健康 ----------
    static bool Report(Options o)
    {
        Log("---- 服务状态 ----");
        var rows = new List<(string Name, int Port, string Url)>
        {
            ("Host", o.HostPort, $"http://localhost:{o.HostPort}/api/Devices"),
            ("Gateway", o.GatewayPort, $"http://localhost:{o.GatewayPort}/api/system/status"),
        };
        if (!o.SkipFrontend) rows.Add(("Frontend", o.FrontendPort, $"http://localhost:{o.FrontendPort}/"));

        var allUp = true;
        foreach (var r in rows)
        {
            var tcp = PortOpen("127.0.0.1", r.Port);
            var http = tcp ? HttpStatus(r.Url, 4000) : "-";
            var ok = tcp && http != "-";
            allUp &= ok;
            Log($"  {r.Name,-9} port={r.Port,-5} tcp={(tcp ? "UP" : "DOWN"),-4} http={http,-4} {(ok ? "[OK]" : "[注意]")}");
        }
        Log(allUp ? "  结论：全部在线" : "  结论：存在未就绪服务，请查看 logs\\*.err.log");
        return allUp;
    }

    static void WaitHttp(string url, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (HttpStatus(url, 2500) != "-") return;
            Thread.Sleep(1500);
        }
    }

    // ---------- 基础工具 ----------
    static bool PortOpen(string host, int port)
    {
        try
        {
            using var c = new TcpClient();
            var iar = c.BeginConnect(host, port, null, null);
            if (!iar.AsyncWaitHandle.WaitOne(700)) return false;
            c.EndConnect(iar);
            return c.Connected;
        }
        catch { return false; }
    }

    static string HttpStatus(string url, int timeoutMs)
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            using var resp = http.GetAsync(url).GetAwaiter().GetResult();
            return ((int)resp.StatusCode).ToString();
        }
        catch { return "-"; }
    }

    static void KillPortOwner(int port)
    {
        // 无目标进程 ID 时按端口结束占用进程（Windows）。
        var cmd = $"$c=Get-NetTCPConnection -State Listen -LocalPort {port} -ErrorAction SilentlyContinue | Select-Object -First 1; if($c){{ Stop-Process -Id $c.OwningProcess -Force }}";
        RunInherited("powershell.exe", "-NoProfile -Command \"" + cmd + "\"", Directory.GetCurrentDirectory());
    }

    static bool CommandOk(string exe, string args, out string output)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    static void StartDetached(string exe, string args, string workDir, string logBase, string pidFile, IDictionary<string, string>? env)
    {
        // 用 cmd.exe 承载重定向，父进程退出后子进程与日志文件仍持续。
        var commandLine = $"\"{exe}\" {args} >> \"{logBase}.out.log\" 2>> \"{logBase}.err.log\"";
        var psi = new ProcessStartInfo("cmd.exe", "/c " + commandLine)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (env is not null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        var p = Process.Start(psi)!;
        Directory.CreateDirectory(Path.GetDirectoryName(pidFile)!);
        File.WriteAllText(pidFile, p.Id.ToString());
        Log($"    pid={p.Id} -> {logBase}.out.log");
    }

    static int RunInherited(string exe, string args, string workDir)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { WorkingDirectory = workDir, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch { return -1; }
    }

    static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Console.WriteLine(line);
        try { File.AppendAllText(_logFile, line + Environment.NewLine, Utf8NoBom); } catch { }
    }

    static bool Has(string[] args, string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    static string? ValueAfter(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    static int IntAfter(string[] args, string name, int fallback)
        => int.TryParse(ValueAfter(args, name), out var v) ? v : fallback;

    static string ResolveRoot(string? hint)
    {
        if (!string.IsNullOrWhiteSpace(hint))
        {
            var full = Path.GetFullPath(hint!);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException("指定目录不存在：" + full);
            return full;
        }
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var cur = start;
            while (!string.IsNullOrEmpty(cur))
            {
                if (File.Exists(Path.Combine(cur, "manifest.json")) ||
                    (Directory.Exists(Path.Combine(cur, "host")) && Directory.Exists(Path.Combine(cur, "gateway"))))
                    return cur;
                var parent = Path.GetDirectoryName(cur);
                if (parent == cur) break;
                cur = parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }

    static void Help()
    {
        Console.WriteLine("InstallRun.cs — 目标机安装/配置端口/检测与容错");
        Console.WriteLine("  --action <a>         install|start|stop|restart|status|check（默认 install）");
        Console.WriteLine("  --root <路径>        包目录（默认自动查找 manifest.json 或 host+gateway）");
        Console.WriteLine("  --host-port <n>      Host 端口（默认 5173）");
        Console.WriteLine("  --gateway-port <n>   Gateway 端口（默认 5087）");
        Console.WriteLine("  --frontend-port <n>  前端端口（默认 4171）");
        Console.WriteLine("  --mysql-port <n>     MySQL 端口（默认 3307）");
        Console.WriteLine("  --influx-port <n>    InfluxDB 端口（默认 8181）");
        Console.WriteLine("  --db-host <h>        数据库主机（默认 127.0.0.1）");
        Console.WriteLine("  --db-user <u>        数据库用户（默认 iott）");
        Console.WriteLine("  --db-password <p>    数据库口令");
        Console.WriteLine("  --skip-db            不写连接串（沿用包内 appsettings）");
        Console.WriteLine("  --skip-frontend      不启动/检测前端");
        Console.WriteLine("  --disable-influx     关闭网关 InfluxDB 写入");
        Console.WriteLine("  --disable-mqtt       关闭网关 MQTT 发布");
        Console.WriteLine("  --provision-mysql    调用包内 deploy/mysql/Start-MySql.ps1 预置本机 MySQL");
        Console.WriteLine("  --force              端口占用时结束占用进程");
        Console.WriteLine("  --check-only         仅预检/状态，不启动");
    }

    sealed class Options
    {
        public string Root = "";
        public int HostPort, GatewayPort, FrontendPort, MySqlPort, InfluxPort;
        public string DbHost = "127.0.0.1", DbUser = "iott", DbPassword = "";
        public bool SkipDb, SkipFrontend, DisableInflux, DisableMqtt, Force, ProvisionMySql;
    }
}
