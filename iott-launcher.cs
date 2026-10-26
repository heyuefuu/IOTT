#:package Spectre.Console@0.49.1
#:property NoWarn=IL2026;IL3050
#:property PublishAot=false

// 单文件脚本：用 .NET 10 file-based app 运行。
//   检查:   dotnet run iott-launcher.cs -- --check
//   打印配置:dotnet run iott-launcher.cs -- --print-config
//   交互:   dotnet run iott-launcher.cs
// 依赖 Spectre.Console（由 #:package 自动还原）。

using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;
using Spectre.Console.Rendering;

Console.OutputEncoding = Encoding.UTF8;
return await Launcher.RunAsync(args);

sealed class Entry
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool ReadOnly { get; set; }
}

sealed class Section
{
    public string Name { get; set; } = "";
    public bool Expanded { get; set; } = true;
    public List<Entry> Items { get; set; } = new();
}

sealed class LauncherConfig
{
    public List<Section> Sections { get; set; } = new();

    public string Get(string section, string key, string fallback = "")
    {
        var s = Sections.FirstOrDefault(x => x.Name == section);
        var i = s?.Items.FirstOrDefault(x => x.Key == key);
        return i?.Value ?? fallback;
    }

    public bool GetBool(string section, string key, bool fallback = false)
        => bool.TryParse(Get(section, key, fallback.ToString()), out var v) ? v : fallback;

    public int GetInt(string section, string key, int fallback)
        => int.TryParse(Get(section, key, ""), out var v) ? v : fallback;

    public void Set(string section, string key, string value)
    {
        var s = Sections.FirstOrDefault(x => x.Name == section);
        if (s is null) { s = new Section { Name = section }; Sections.Add(s); }
        var i = s.Items.FirstOrDefault(x => x.Key == key);
        if (i is null) s.Items.Add(new Entry { Key = key, Value = value });
        else i.Value = value;
    }

    private static string ReadJsonString(string path, params string[] names)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var cur = doc.RootElement;
            foreach (var name in names)
            {
                if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(name, out var next)) return "";
                cur = next;
            }
            return cur.ValueKind == JsonValueKind.String ? cur.GetString() ?? "" : cur.ToString();
        }
        catch { return ""; }
    }

    private static string FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var full = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return "";
    }

    private static string ResolveDockerExe()
    {
        var found = FindOnPath("docker.exe");
        if (!string.IsNullOrEmpty(found)) return found;
        foreach (var candidate in new[]
                 {
                     @"D:\docker\resources\bin\docker.exe",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe"),
                 })
            if (File.Exists(candidate)) return candidate;
        return "docker";
    }

    private static string ResolveDockerDesktopExe()
    {
        foreach (var candidate in new[]
                 {
                     @"D:\docker\Docker Desktop.exe",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe"),
                 })
            if (File.Exists(candidate)) return candidate;
        // 从 docker.exe 所在目录向上找 Docker Desktop.exe（典型：<root>\resources\bin\docker.exe）
        var dir = Path.GetDirectoryName(ResolveDockerExe());
        for (var i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, "Docker Desktop.exe");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return "";
    }

    public static LauncherConfig CreateDefault(string root)
    {
        var hostLocal = Path.Combine(root, "Host", "src", "IndustrialIoT.Host", "appsettings.Local.json");
        var gwLocal = Path.Combine(root, "MachineConnectionApi20260423", "MachineConnectionApi", "MachineConnectionApi", "appsettings.Local.json");
        var gwSettings = Path.Combine(root, "MachineConnectionApi20260423", "MachineConnectionApi", "MachineConnectionApi", "appsettings.json");
        var instance = Path.Combine(root, "deploy", "mysql", ".runtime", "instance.json");

        var mysqlBin = ReadJsonString(instance, "Bin");
        var rootPwd = ReadJsonString(instance, "RootPassword");
        var appPwd = ReadJsonString(instance, "AppPassword");
        var influxToken = ReadJsonString(gwSettings, "InfluxDB", "Token");
        // Mock 仅存在于带 tools/IottDevMock 的分支；main 无该项目，可沿用既有已构建的测试 Mock。
        var mockProject = Path.Combine(root, "tools", "IottDevMock", "IottDevMock.csproj");
        var mockDll = Path.Combine(root, "tools", "IottDevMock", "bin", "Debug", "net8.0", "IottDevMock.dll");

        var cfg = new LauncherConfig();

        var run = new Section { Name = "运行 (Launcher)" };
        run.Items.Add(new Entry { Key = "HostPort", Value = "5173" });
        run.Items.Add(new Entry { Key = "GatewayPort", Value = "5087" });
        run.Items.Add(new Entry { Key = "FrontendPort", Value = "4171" });
        run.Items.Add(new Entry { Key = "MySqlPort", Value = "3307" });
        run.Items.Add(new Entry { Key = "MockPort", Value = "5199" });
        run.Items.Add(new Entry { Key = "InfluxPort", Value = "8181" });
        cfg.Sections.Add(run);

        var toggles = new Section { Name = "选项 (Toggles)" };
        toggles.Items.Add(new Entry { Key = "SkipMySql", Value = "false" });
        toggles.Items.Add(new Entry { Key = "SkipFrontend", Value = "false" });
        toggles.Items.Add(new Entry { Key = "Force", Value = "false" });
        toggles.Items.Add(new Entry { Key = "NoBuild", Value = "false" });
        toggles.Items.Add(new Entry { Key = "Background", Value = "false" });
        toggles.Items.Add(new Entry { Key = "UseMock", Value = (File.Exists(mockProject) || File.Exists(mockDll)) ? "true" : "false" });
        toggles.Items.Add(new Entry { Key = "GatewayUseMock", Value = "false" });
        toggles.Items.Add(new Entry { Key = "UseInflux", Value = "false" });
        toggles.Items.Add(new Entry { Key = "VerifySimulate", Value = "true" });
        cfg.Sections.Add(toggles);

        var mysql = new Section { Name = "MySQL" };
        mysql.Items.Add(new Entry { Key = "Bin", Value = mysqlBin });
        mysql.Items.Add(new Entry { Key = "DataDir", Value = "deploy/mysql/.runtime/data" });
        mysql.Items.Add(new Entry { Key = "ServerVersion", Value = "8.4.0" });
        mysql.Items.Add(new Entry { Key = "Database1", Value = "IndustrialIoT" });
        mysql.Items.Add(new Entry { Key = "Database2", Value = "MachineCollection" });
        mysql.Items.Add(new Entry { Key = "AppUser", Value = "iott" });
        mysql.Items.Add(new Entry { Key = "AppPassword", Value = appPwd });
        mysql.Items.Add(new Entry { Key = "RootPassword", Value = rootPwd });
        cfg.Sections.Add(mysql);

        var host = new Section { Name = "Host (IndustrialIoT.Host)" };
        host.Items.Add(new Entry { Key = "ConnectionStrings.Default", Value = ReadJsonString(hostLocal, "ConnectionStrings", "Default") });
        host.Items.Add(new Entry { Key = "Urls", Value = "http://localhost:5173" });
        host.Items.Add(new Entry { Key = "AllowedHosts", Value = "*" });
        host.Items.Add(new Entry { Key = "Database.ServerVersion", Value = "8.4.0" });
        host.Items.Add(new Entry { Key = "Serilog.MinimumLevel.Default", Value = "Information" });
        host.Items.Add(new Entry { Key = "Serilog.Override.Microsoft.AspNetCore", Value = "Warning" });
        host.Items.Add(new Entry { Key = "Serilog.Override.Microsoft.EntityFrameworkCore", Value = "Warning" });
        host.Items.Add(new Entry { Key = "Gskrm.Mode", Value = "Ipc" });
        host.Items.Add(new Entry { Key = "Gskrm.IpcBaseUrl", Value = "http://127.0.0.1:39123" });
        host.Items.Add(new Entry { Key = "RobotAddressMaps.Huazhong.Nodes", Value = "[]" });
        host.Items.Add(new Entry { Key = "DataOutput.Mqtt.Enabled", Value = "(代码未实现，恒为启用)", ReadOnly = false });
        host.Items.Add(new Entry { Key = "DataOutput.Mqtt.BrokerHost", Value = "localhost" });
        host.Items.Add(new Entry { Key = "DataOutput.Mqtt.BrokerPort", Value = "1883" });
        host.Items.Add(new Entry { Key = "DataOutput.Mqtt.ClientId", Value = "industrial-iot-local" });
        host.Items.Add(new Entry { Key = "DataOutput.Mqtt.TopicPattern", Value = "industrial-iot/devices/{deviceId}/data" });
        host.Items.Add(new Entry { Key = "DataOutput.Mqtt.Username", Value = "" });
        host.Items.Add(new Entry { Key = "DataOutput.Mqtt.Password", Value = "" });
        host.Items.Add(new Entry { Key = "DataOutput.Database.Enabled", Value = "false" });
        host.Items.Add(new Entry { Key = "DataOutput.HttpWebhook.Enabled", Value = "false" });
        host.Items.Add(new Entry { Key = "DataOutput.HttpWebhook.EndpointUrl", Value = "" });
        host.Items.Add(new Entry { Key = "DataOutput.HttpWebhook.BearerToken", Value = "" });
        host.Items.Add(new Entry { Key = "DataOutput.Kafka.Enabled", Value = "false" });
        host.Items.Add(new Entry { Key = "DataOutput.Kafka.BootstrapServers", Value = "localhost:9092" });
        host.Items.Add(new Entry { Key = "DataOutput.Kafka.Topic", Value = "industrial-iot-data" });
        host.Items.Add(new Entry { Key = "DataOutput.Kafka.Acks", Value = "Leader" });
        host.Items.Add(new Entry { Key = "DataOutput.Kafka.LingerMs", Value = "5" });
        host.Items.Add(new Entry { Key = "DataOutput.Kafka.BatchSize", Value = "65536" });
        host.Items.Add(new Entry { Key = "DataOutput.Kafka.SaslUsername", Value = "" });
        host.Items.Add(new Entry { Key = "DataOutput.Kafka.SaslPassword", Value = "" });
        cfg.Sections.Add(host);

        var gw = new Section { Name = "Gateway (MachineConnectionApi)" };
        gw.Items.Add(new Entry { Key = "ConnectionStrings.MachineCollection", Value = ReadJsonString(gwLocal, "ConnectionStrings", "MachineCollection") });
        gw.Items.Add(new Entry { Key = "Urls", Value = "http://localhost:5087" });
        gw.Items.Add(new Entry { Key = "AllowedHosts", Value = "*" });
        gw.Items.Add(new Entry { Key = "Database.ServerVersion", Value = "8.4.0" });
        gw.Items.Add(new Entry { Key = "Logging.Default", Value = "Information" });
        gw.Items.Add(new Entry { Key = "Logging.Microsoft.AspNetCore", Value = "Warning" });
        gw.Items.Add(new Entry { Key = "IndustrialIoT.BaseUrl", Value = "http://localhost:5173" });
        gw.Items.Add(new Entry { Key = "IndustrialIoT.DevicesPath", Value = "api/Devices" });
        gw.Items.Add(new Entry { Key = "IndustrialIoT.DataPath", Value = "api/data" });
        gw.Items.Add(new Entry { Key = "IndustrialIoT.AddressSpacePath", Value = "api/addressspace" });
        gw.Items.Add(new Entry { Key = "IndustrialIoT.CollectionConfigPath", Value = "api/collection-config" });
        gw.Items.Add(new Entry { Key = "IndustrialIoT.CollectionPath", Value = "api/collection" });
        gw.Items.Add(new Entry { Key = "IndustrialIoT.BatchImportPath", Value = "api/batch-import" });
        gw.Items.Add(new Entry { Key = "IndustrialIoT.ConnectionVerificationPath", Value = "api/connection-verification" });
        gw.Items.Add(new Entry { Key = "IndustrialIoT.PlcCapabilitiesPath", Value = "api/plc/capabilities" });
        gw.Items.Add(new Entry { Key = "IndustrialIoT.NCLinkPath", Value = "api/nclink" });
        gw.Items.Add(new Entry { Key = "IndustrialIoT.ProgramTransferPath", Value = "api/program-transfer" });
        gw.Items.Add(new Entry { Key = "IndustrialIoT.DeviceSeedPath", Value = "" });
        gw.Items.Add(new Entry { Key = "Mqtt.Enabled", Value = "true" });
        gw.Items.Add(new Entry { Key = "Mqtt.Host", Value = "127.0.0.1" });
        gw.Items.Add(new Entry { Key = "Mqtt.Port", Value = "1883" });
        gw.Items.Add(new Entry { Key = "Mqtt.ClientId", Value = "MachineConnectionApi" });
        gw.Items.Add(new Entry { Key = "Mqtt.Username", Value = "" });
        gw.Items.Add(new Entry { Key = "Mqtt.Password", Value = "" });
        gw.Items.Add(new Entry { Key = "Mqtt.UseTls", Value = "false" });
        gw.Items.Add(new Entry { Key = "Mqtt.TopicPrefix", Value = "machines/telemetry" });
        gw.Items.Add(new Entry { Key = "Mqtt.QualityOfService", Value = "1" });
        gw.Items.Add(new Entry { Key = "Mqtt.ConnectTimeoutSeconds", Value = "10" });
        gw.Items.Add(new Entry { Key = "InfluxDB.Enabled", Value = "true" });
        gw.Items.Add(new Entry { Key = "InfluxDB.Url", Value = "http://127.0.0.1:8181" });
        gw.Items.Add(new Entry { Key = "InfluxDB.Token", Value = influxToken });
        gw.Items.Add(new Entry { Key = "InfluxDB.Org", Value = "default" });
        gw.Items.Add(new Entry { Key = "InfluxDB.Bucket", Value = "machine_collection" });
        gw.Items.Add(new Entry { Key = "InfluxDB.Measurement", Value = "datapoint" });
        gw.Items.Add(new Entry { Key = "InfluxDB.WriteTimeoutSeconds", Value = "15" });
        gw.Items.Add(new Entry { Key = "InfluxDB.WriteRetryCount", Value = "2" });
        gw.Items.Add(new Entry { Key = "InfluxDB.WriteRetryDelayMs", Value = "500" });
        gw.Items.Add(new Entry { Key = "Env.EVALUATION_PDF_FONT", Value = "" });
        cfg.Sections.Add(gw);

        var influx = new Section { Name = "InfluxDB (Docker)" };
        influx.Items.Add(new Entry { Key = "DockerExe", Value = ResolveDockerExe() });
        influx.Items.Add(new Entry { Key = "DockerDesktopExe", Value = ResolveDockerDesktopExe() });
        influx.Items.Add(new Entry { Key = "ComposeFile", Value = "deploy/influxdb/compose.yaml" });
        influx.Items.Add(new Entry { Key = "StartScript", Value = "deploy/influxdb/Start-InfluxDb.ps1" });
        influx.Items.Add(new Entry { Key = "BindAddress", Value = "127.0.0.1" });
        influx.Items.Add(new Entry { Key = "AutoStartDockerDesktop", Value = "true" });
        influx.Items.Add(new Entry { Key = "StartTimeoutSeconds", Value = "120" });
        cfg.Sections.Add(influx);

        var fe = new Section { Name = "Frontend (Vite)" };
        fe.Items.Add(new Entry { Key = "Dir", Value = "code20260423/code" });
        fe.Items.Add(new Entry { Key = "Port", Value = "4171" });
        fe.Items.Add(new Entry { Key = "Host", Value = "0.0.0.0" });
        fe.Items.Add(new Entry { Key = "VITE_MACHINE_CONNECTION_API", Value = "/machine-connection" });
        fe.Items.Add(new Entry { Key = "VITE_MACHINE_CONNECTION_PROXY_TARGET", Value = "http://localhost:5087" });
        fe.Items.Add(new Entry { Key = "VITE_MACHINE_CONNECTION_PORT", Value = "5087" });
        fe.Items.Add(new Entry { Key = "VITE_INDUSTRIAL_IOT_API", Value = "/industrial-iot" });
        fe.Items.Add(new Entry { Key = "VITE_INDUSTRIAL_IOT_PROXY_TARGET", Value = "http://localhost:5173" });
        fe.Items.Add(new Entry { Key = "VITE_INDUSTRIAL_IOT_PORT", Value = "5173" });
        fe.Items.Add(new Entry { Key = "VITE_APP_TITLE", Value = "" });
        fe.Items.Add(new Entry { Key = "VITE_APP_API_BASE_URL", Value = "" });
        fe.Items.Add(new Entry { Key = "VITE_APP_DEBUG", Value = "" });
        cfg.Sections.Add(fe);

        return cfg;
    }

    public static LauncherConfig Load(string path, string root)
    {
        var defaults = CreateDefault(root);
        if (!File.Exists(path)) return defaults;
        try
        {
            var loaded = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path)) ?? defaults;
            // 合并默认项，保证版本升级后新键仍在
            foreach (var s in defaults.Sections)
            {
                var target = loaded.Sections.FirstOrDefault(x => x.Name == s.Name);
                if (target is null) { loaded.Sections.Add(s); continue; }
                foreach (var i in s.Items)
                    if (target.Items.All(x => x.Key != i.Key)) target.Items.Add(i);
            }
            return loaded;
        }
        catch { return defaults; }
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }
}

sealed record ServiceStatus(string Name, bool Up, string Detail, string Endpoint);

static class Probe
{
    public static bool TcpOpen(string host, int port, int timeoutMs = 700)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(timeoutMs) && client.Connected;
        }
        catch { return false; }
    }

    public static bool HttpReachable(string url, int timeoutMs = 1500)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            using var resp = http.GetAsync(url).GetAwaiter().GetResult();
            return (int)resp.StatusCode is >= 200 and < 500;
        }
        catch { return false; }
    }

    /// <summary>Docker 引擎是否可用（docker version 能拿到 Server 版本）。</summary>
    public static bool DockerDaemonUp(string dockerExe, int timeoutMs = 5000)
    {
        if (string.IsNullOrWhiteSpace(dockerExe)) return false;
        try
        {
            var psi = new ProcessStartInfo(dockerExe, "version --format {{.Server.Version}}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public static List<ServiceStatus> Check(LauncherConfig cfg)
    {
        var hostPort = cfg.GetInt("运行 (Launcher)", "HostPort", 5173);
        var gwPort = cfg.GetInt("运行 (Launcher)", "GatewayPort", 5087);
        var fePort = cfg.GetInt("运行 (Launcher)", "FrontendPort", 4171);
        var myPort = cfg.GetInt("运行 (Launcher)", "MySqlPort", 3307);

        var myUp = Probe.TcpOpen("127.0.0.1", myPort);
        var list = new List<ServiceStatus>
        {
            new("MySQL", myUp, myUp ? "端口在监听" : "未监听", $"127.0.0.1:{myPort}"),
        };

        var influxPort = cfg.GetInt("运行 (Launcher)", "InfluxPort", 8181);
        var influxUp = Probe.TcpOpen("127.0.0.1", influxPort);
        var influxHealth = influxUp && Probe.HttpReachable($"http://127.0.0.1:{influxPort}/health");
        list.Add(new("InfluxDB", influxUp,
            influxUp ? (influxHealth ? "HTTP 正常" : "端口在监听") : "未监听",
            $"http://127.0.0.1:{influxPort}"));

        var hostUp = Probe.TcpOpen("127.0.0.1", hostPort);
        var hostHttp = hostUp && Probe.HttpReachable($"http://localhost:{hostPort}/api/Devices");
        list.Add(new("Host", hostUp, hostUp ? (hostHttp ? "HTTP 正常" : "端口在监听") : "未监听", $"http://localhost:{hostPort}"));

        var gwUp = Probe.TcpOpen("127.0.0.1", gwPort);
        list.Add(new("Gateway", gwUp, gwUp ? "端口在监听" : "未监听", $"http://localhost:{gwPort}"));

        var feUp = Probe.TcpOpen("127.0.0.1", fePort);
        list.Add(new("Frontend", feUp, feUp ? "端口在监听" : "未监听", $"http://localhost:{fePort}"));

        var mockPort = cfg.GetInt("运行 (Launcher)", "MockPort", 5199);
        var mockUp = Probe.TcpOpen("127.0.0.1", mockPort);
        list.Add(new("Mock", mockUp, mockUp ? "端口在监听" : "未监听", $"http://localhost:{mockPort}"));

        return list;
    }
}

static class Runner
{
    public static int RunCaptured(string exe, string args, string workDir, Action<string> log, int timeoutMs = 0, IDictionary<string, string>? extraEnv = null)
    {
        log($"$ {exe} {args}");
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (extraEnv is not null)
            foreach (var kv in extraEnv) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (timeoutMs > 0) p.WaitForExit(timeoutMs); else p.WaitForExit();
        return p.HasExited ? p.ExitCode : -1;
    }

    public static Process StartWindow(string exe, string args, string workDir, bool background)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workDir,
            UseShellExecute = !background,
            CreateNoWindow = background,
        };
        return Process.Start(psi)!;
    }

    public static int? PidOnPort(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (!trimmed.Contains($":{port}")) continue;
                if (!trimmed.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5 && int.TryParse(parts[^1], out var pid)) return pid;
            }
        }
        catch { }
        return null;
    }

    public static void StopPort(int port, Action<string> log)
    {
        var pid = PidOnPort(port);
        if (pid is null) { log($"端口 {port} 无监听进程"); return; }
        log($"结束端口 {port} 的进程 PID {pid}");
        RunCaptured("taskkill", $"/PID {pid} /T /F", ".", _ => { }, 15000);
    }
}

static class Launcher
{
    public static async Task<int> RunAsync(string[] args)
    {
        var check = args.Contains("--check");
        var printConfig = args.Contains("--print-config");
        var help = args.Contains("--help") || args.Contains("-h");
        var rootArg = ValueAfter(args, "--root");

        var root = ResolveRoot(rootArg);
        if (root is null)
        {
            AnsiConsole.MarkupLine("[red]未找到仓库根目录（应包含 Host/IndustrialIoT.sln）。用 --root <路径> 指定。[/]");
            return 1;
        }

        var configPath = Path.Combine(root, "iott-launcher.config.json");
        var cfg = LauncherConfig.Load(configPath, root);

        if (help)
        {
            PrintHelp();
            return 0;
        }
        if (printConfig)
        {
            DumpConfig(cfg);
            return 0;
        }
        if (check)
        {
            PrintStatus(Probe.Check(cfg));
            return 0;
        }

        // 非交互启动/停止：脚本化地跑「修正 → 构建 → 启动」或停止各服务，便于无 TUI 环境下跑通原始工程。
        if (args.Contains("--start") || args.Contains("--stop"))
        {
            var headless = new Tui(root, configPath, cfg);
            var (status, log) = headless.RunHeadless(args.Contains("--start"));
            PrintStatus(status);
            foreach (var line in log) Console.WriteLine("  " + line);
            return 0;
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            AnsiConsole.MarkupLine("[yellow]当前非交互式终端（输入/输出被重定向）。[/]");
            PrintStatus(Probe.Check(cfg));
            AnsiConsole.MarkupLine("在真实终端中运行 `dotnet run iott-launcher.cs` 进入交互界面；或用 [blue]--check[/] / [blue]--print-config[/]。");
            return 0;
        }

        return await new Tui(root, configPath, cfg).RunAsync();
    }

    static string? ValueAfter(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    static string? ResolveRoot(string? hint)
    {
        if (!string.IsNullOrWhiteSpace(hint) && Directory.Exists(hint)) return Path.GetFullPath(hint);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Host", "IndustrialIoT.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
        }
        return null;
    }

    static void PrintStatus(List<ServiceStatus> statuses)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]服务状态[/]");
        table.AddColumn("服务");
        table.AddColumn("状态");
        table.AddColumn("详情");
        table.AddColumn("地址");
        foreach (var s in statuses)
            table.AddRow(
                Markup.Escape(s.Name),
                s.Up ? "[green]Running[/]" : "[grey]Stopped[/]",
                Markup.Escape(s.Detail),
                Markup.Escape(s.Endpoint));
        AnsiConsole.Write(table);
    }

    static void DumpConfig(LauncherConfig cfg)
    {
        var total = 0;
        foreach (var s in cfg.Sections)
        {
            AnsiConsole.MarkupLine($"[yellow bold]▼ {Markup.Escape(s.Name)}[/]");
            foreach (var i in s.Items)
                AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(i.Key)}[/] = {Markup.Escape(i.Value)}");
            total += s.Items.Count;
        }
        AnsiConsole.MarkupLine($"[bold green]共 {total} 项配置，{cfg.Sections.Count} 个分区[/]");
    }

    static void PrintHelp()
    {
        AnsiConsole.MarkupLine("[bold]iott-launcher[/] — 检查/修正/启动 MySQL·InfluxDB·Host·Gateway·前端");
        AnsiConsole.MarkupLine("  [blue]--check[/]         打印各服务状态后退出");
        AnsiConsole.MarkupLine("  [blue]--start[/]         非交互执行「修正 → 构建 → 启动」后退出");
        AnsiConsole.MarkupLine("  [blue]--stop[/]          非交互停止各服务后退出");
        AnsiConsole.MarkupLine("  [blue]--print-config[/]  打印全部配置项后退出");
        AnsiConsole.MarkupLine("  [blue]--root <路径>[/]   指定仓库根目录");
        AnsiConsole.MarkupLine("  [blue]--help[/]          显示本帮助");
        AnsiConsole.MarkupLine("无参数时进入交互界面。");
    }
}

sealed class Tui
{
    private readonly string _root;
    private readonly string _configPath;
    private readonly LauncherConfig _cfg;
    private readonly List<string> _log = new();
    private int _selected;
    private int _scroll;
    private List<ServiceStatus> _status = new();
    private bool _buttonsFocused;
    private int _buttonIndex;
    private readonly Dictionary<string, string> _buttonState = new(StringComparer.Ordinal);

    public Tui(string root, string configPath, LauncherConfig cfg)
    {
        _root = root; _configPath = configPath; _cfg = cfg;
    }

    private void Log(string message)
    {
        foreach (var line in message.Replace("\r", "").Split('\n'))
        {
            if (line.Length == 0) continue;
            _log.Add(line);
            if (_log.Count > 400) _log.RemoveAt(0);
        }
    }

    private List<(bool Header, int Si, int Ei)> BuildRows()
    {
        var rows = new List<(bool, int, int)>();
        for (var si = 0; si < _cfg.Sections.Count; si++)
        {
            rows.Add((true, si, -1));
            if (_cfg.Sections[si].Expanded)
                for (var ei = 0; ei < _cfg.Sections[si].Items.Count; ei++)
                    rows.Add((false, si, ei));
        }
        return rows;
    }

    public async Task<int> RunAsync()
    {
        _status = Probe.Check(_cfg);
        Log("就绪。Tab 聚焦按钮 · Enter 触发 · C 检查 F 修正 B 构建 S 启动 X 停止 R 全流程 D 保存");

        while (true)
        {
            var rows = BuildRows();
            if (_selected >= rows.Count) _selected = rows.Count - 1;
            if (_selected < 0) _selected = 0;

            Render(rows);

            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Tab)
            {
                _buttonsFocused = !_buttonsFocused;
                continue;
            }

            if (_buttonsFocused)
            {
                switch (key.Key)
                {
                    case ConsoleKey.LeftArrow:
                    case ConsoleKey.UpArrow: MoveButton(-1); break;
                    case ConsoleKey.RightArrow:
                    case ConsoleKey.DownArrow: MoveButton(1); break;
                    case ConsoleKey.Enter: TriggerButton(_buttonIndex); break;
                    case ConsoleKey.Escape: _buttonsFocused = false; break;
                    case ConsoleKey.Q:
                        Save();
                        AnsiConsole.Clear();
                        return 0;
                    default: HandleLetter(key.Key); break;
                }
                continue;
            }

            switch (key.Key)
            {
                case ConsoleKey.UpArrow: Move(-1); break;
                case ConsoleKey.DownArrow: Move(1); break;
                case ConsoleKey.PageUp: Move(-PageSize()); break;
                case ConsoleKey.PageDown: Move(PageSize()); break;
                case ConsoleKey.Home: _selected = 0; break;
                case ConsoleKey.End: _selected = rows.Count - 1; break;
                case ConsoleKey.LeftArrow: ToggleSection(rows, false); break;
                case ConsoleKey.RightArrow: ToggleSection(rows, true); break;
                case ConsoleKey.Enter: Activate(rows); break;
                case ConsoleKey.Spacebar: ToggleSection(rows, null); break;
                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    Save();
                    AnsiConsole.Clear();
                    return 0;
                default: HandleLetter(key.Key); break;
            }
        }
    }

    /// <summary>非交互启动/停止，返回最终服务状态与日志，供 --start/--stop 使用。</summary>
    public (List<ServiceStatus> Status, IReadOnlyList<string> Log) RunHeadless(bool start)
    {
        _status = Probe.Check(_cfg);
        Log(start ? "== 非交互启动：修正 → 构建 → 启动 ==" : "== 非交互停止 ==");
        if (start) StartAll(); else StopAll();
        _status = Probe.Check(_cfg);
        return (_status, _log.ToList());
    }

    private int PageSize()
    {
        var body = BodyHeight();
        return Math.Max(1, body - 1);
    }

    private void Move(int delta)
    {
        var rows = BuildRows();
        _selected = Math.Clamp(_selected + delta, 0, Math.Max(0, rows.Count - 1));
    }

    private void ToggleSection(List<(bool Header, int Si, int Ei)> rows, bool? expand)
    {
        if (_selected < 0 || _selected >= rows.Count) return;
        var r = rows[_selected];
        if (!r.Header) return;
        var s = _cfg.Sections[r.Si];
        s.Expanded = expand ?? !s.Expanded;
    }

    /// <summary>执行选中行：折叠分区、切换布尔项，或进入内联编辑。</summary>
    private void Activate(List<(bool Header, int Si, int Ei)> rows)
    {
        if (_selected < 0 || _selected >= rows.Count) return;
        var r = rows[_selected];
        if (r.Header)
        {
            _cfg.Sections[r.Si].Expanded = !_cfg.Sections[r.Si].Expanded;
            return;
        }

        var entry = _cfg.Sections[r.Si].Items[r.Ei];
        if (entry.ReadOnly) { Log($"{entry.Key} 为只读"); return; }

        if (bool.TryParse(entry.Value, out var b))
        {
            entry.Value = (!b).ToString().ToLowerInvariant();
            Save();
            return;
        }

        // 内联编辑：以现有值预填充；Enter/Esc 都保存并返回主菜单
        entry.Value = EditInPlace(entry.Key, entry.Value);
        Save();
        Log($"已设置 {entry.Key} = {entry.Value}");
        _status = Probe.Check(_cfg);
    }

    /// <summary>
    /// 单行编辑器：以 initial 作为初始内容（预填充）；Enter 确认，Esc 保存当前内容并返回菜单。
    /// </summary>
    private string EditInPlace(string label, string initial)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[grey]编辑模式：←→/Home/End 移动  Backspace/Delete 删除  [white]Enter[/] 确认  [white]Esc[/] 保存并返回菜单[/]");
        var prompt = label + " = ";
        Console.Write(prompt);

        var buffer = new StringBuilder(initial ?? string.Empty);
        var cursor = buffer.Length;
        var startLeft = SafeCursorLeft();
        var row = SafeCursorTop();

        void Redraw()
        {
            var width = SafeWindowWidth();
            var avail = Math.Max(1, width - startLeft - 1);
            var text = buffer.ToString();
            var display = text.Length <= avail ? text : text.Substring(0, avail);
            try
            {
                Console.SetCursorPosition(startLeft, row);
                Console.Write(display);
                if (display.Length < avail) Console.Write(new string(' ', avail - display.Length));
                Console.SetCursorPosition(startLeft + Math.Min(cursor, avail), row);
            }
            catch { /* 光标不可用时忽略 */ }
        }

        Redraw();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                case ConsoleKey.Escape:
                    return buffer.ToString();
                case ConsoleKey.LeftArrow:
                    if (cursor > 0) cursor--;
                    break;
                case ConsoleKey.RightArrow:
                    if (cursor < buffer.Length) cursor++;
                    break;
                case ConsoleKey.Home:
                    cursor = 0;
                    break;
                case ConsoleKey.End:
                    cursor = buffer.Length;
                    break;
                case ConsoleKey.Backspace:
                    if (cursor > 0) { buffer.Remove(cursor - 1, 1); cursor--; }
                    break;
                case ConsoleKey.Delete:
                    if (cursor < buffer.Length) buffer.Remove(cursor, 1);
                    break;
                default:
                    if (!char.IsControl(key.KeyChar)) { buffer.Insert(cursor, key.KeyChar); cursor++; }
                    break;
            }
            Redraw();
        }
    }

    private static int SafeWindowWidth()
    {
        try { return Math.Max(20, Console.WindowWidth); } catch { return 80; }
    }

    private static int SafeCursorLeft()
    {
        try { return Console.CursorLeft; } catch { return 0; }
    }

    private static int SafeCursorTop()
    {
        try { return Console.CursorTop; } catch { return 0; }
    }

    private void Save() => _cfg.Save(_configPath);

    private bool SkipMySql => _cfg.GetBool("选项 (Toggles)", "SkipMySql");
    private bool SkipFrontend => _cfg.GetBool("选项 (Toggles)", "SkipFrontend");
    private bool NoBuild => _cfg.GetBool("选项 (Toggles)", "NoBuild");
    private bool Background => _cfg.GetBool("选项 (Toggles)", "Background");
    private bool Force => _cfg.GetBool("选项 (Toggles)", "Force");
    private bool UseMock => _cfg.GetBool("选项 (Toggles)", "UseMock", true);
    private bool GatewayUseMock => _cfg.GetBool("选项 (Toggles)", "GatewayUseMock", false);
    private int MySqlPort => _cfg.GetInt("运行 (Launcher)", "MySqlPort", 3307);
    private int HostPort => _cfg.GetInt("运行 (Launcher)", "HostPort", 5173);
    private int GatewayPort => _cfg.GetInt("运行 (Launcher)", "GatewayPort", 5087);
    private int FrontendPort => _cfg.GetInt("运行 (Launcher)", "FrontendPort", 4171);
    private int MockPort => _cfg.GetInt("运行 (Launcher)", "MockPort", 5199);
    private int InfluxPort => _cfg.GetInt("运行 (Launcher)", "InfluxPort", 8181);
    private bool UseInflux => _cfg.GetBool("选项 (Toggles)", "UseInflux", false);
    // 验证模块模拟测试开关：同步写入网关 appsettings.Local.json 的 Verify:Simulate（测试用，生产应为 false）。
    private bool VerifySimulate => _cfg.GetBool("选项 (Toggles)", "VerifySimulate", true);
    // Mock 源工程仅存在于带 tools/IottDevMock 的分支；main 缺失时不参与构建，沿用既有已构建的 Mock 二进制。
    private bool MockProjectExists => File.Exists(Path.Combine(_root, "tools", "IottDevMock", "IottDevMock.csproj"));
    // 验证模拟适配器属分支特性；main 无该实现时跳过写入 Verify:Simulate，避免污染原始配置。
    private bool VerifySimulateSupported => File.Exists(Path.Combine(_root,
        "MachineConnectionApi20260423", "MachineConnectionApi", "MachineConnectionApi", "Services", "SimulatedVerifyMeasurementAdapter.cs"));
    private string DockerExe => _cfg.Get("InfluxDB (Docker)", "DockerExe", "docker");
    private string DockerDesktopExe => _cfg.Get("InfluxDB (Docker)", "DockerDesktopExe", "");
    private string InfluxComposeFile => Path.Combine(_root, _cfg.Get("InfluxDB (Docker)", "ComposeFile", "deploy/influxdb/compose.yaml"));
    private string InfluxStartScript => Path.Combine(_root, _cfg.Get("InfluxDB (Docker)", "StartScript", "deploy/influxdb/Start-InfluxDb.ps1"));
    private string InfluxBindAddress => _cfg.Get("InfluxDB (Docker)", "BindAddress", "127.0.0.1");
    private bool AutoStartDockerDesktop => _cfg.GetBool("InfluxDB (Docker)", "AutoStartDockerDesktop", true);
    private int InfluxStartTimeout => _cfg.GetInt("InfluxDB (Docker)", "StartTimeoutSeconds", 120);

    /// <summary>TUI 按钮（Spectre 无原生按钮控件，用 Markup 绘制 + 键盘聚焦触发）。</summary>
    private sealed record Btn(string Id, string Text, bool IsService, string? ServiceName);
    private List<Btn> Buttons() =>
    [
        new("check", "检查", false, null),
        new("fix", "修正", false, null),
        new("build", "构建", false, null),
        new("runall", "全流程", false, null),
        new("mysql", "MySQL", true, "MySQL"),
        new("host", "Host", true, "Host"),
        new("gateway", "Gateway", true, "Gateway"),
        new("frontend", "前端", true, "Frontend"),
        new("mock", "Mock", true, "Mock"),
        new("influx", "Influx", true, "InfluxDB"),
    ];

    private bool ServiceUp(string? name) =>
        name is not null && (_status.FirstOrDefault(s => s.Name == name)?.Up ?? false);

    /// <summary>
    /// 确保本地数据库配置可用：若 MySQL 已在运行且本项目已生成 appsettings.Local.json，
    /// 直接复用（跳过实例修正，避免端口被占时 Start-MySql.ps1 抛错导致后端拿不到配置）；否则执行修正脚本。
    /// </summary>
    private void EnsureMySql()
    {
        if (SkipMySql) return;
        var hostLocal = Path.Combine(_root, "Host", "src", "IndustrialIoT.Host", "appsettings.Local.json");
        var gwLocal = Path.Combine(_root, "MachineConnectionApi20260423", "MachineConnectionApi", "MachineConnectionApi", "appsettings.Local.json");
        if (Probe.TcpOpen("127.0.0.1", MySqlPort) && File.Exists(hostLocal) && File.Exists(gwLocal))
        {
            Log($"检测到 MySQL({MySqlPort}) 已在运行且本地配置存在，复用实例（跳过修正）");
            return;
        }
        Log("== 修正 MySQL ==");
        Runner.RunCaptured("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(_root, "deploy", "mysql", "Start-MySql.ps1")}\"", _root, Log);
        Runner.RunCaptured("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(_root, "deploy", "mysql", "Import-InitialData.ps1")}\"", _root, Log);
    }

    private void Ensure()
    {
        EnsureMySql();
        if (!SkipFrontend)
        {
            var dir = FrontendDir();
            if (!Directory.Exists(Path.Combine(dir, "node_modules")))
            {
                Log("== 安装前端依赖 (npm ci) ==");
                Runner.RunCaptured("cmd.exe", "/c npm ci", dir, Log);
            }
            else Log("前端依赖已存在，跳过");
        }
        _status = Probe.Check(_cfg);
    }

    private string FrontendDir() => Path.Combine(_root, _cfg.Get("Frontend (Vite)", "Dir", "code20260423/code"));

    private void BuildAll()
    {
        foreach (var (name, proj) in Backends())
        {
            Log($"== 构建 {name} ==");
            Runner.RunCaptured("dotnet", $"build \"{proj}\" --nologo -v quiet", _root, Log);
        }
    }

    private IEnumerable<(string Name, string Proj)> Backends()
    {
        yield return ("Host", Path.Combine(_root, "Host", "src", "IndustrialIoT.Host", "IndustrialIoT.Host.csproj"));
        yield return ("Gateway", Path.Combine(_root, "MachineConnectionApi20260423", "MachineConnectionApi", "MachineConnectionApi", "MachineConnectionApi.csproj"));
        if (MockProjectExists)
            yield return ("Mock", Path.Combine(_root, "tools", "IottDevMock", "IottDevMock.csproj"));
    }

    private void StartAll()
    {
        if (Force) StopAll();
        Ensure();
        if (!NoBuild) BuildAll();
        StartHost();
        StartGateway();
        if (!SkipFrontend) StartFrontend();
        if (UseMock) StartMock();
        if (UseInflux) StartInflux();
        _status = Probe.Check(_cfg);
    }

    private void StartHost()
    {
        var port = _cfg.GetInt("运行 (Launcher)", "HostPort", 5173);
        if (Probe.TcpOpen("127.0.0.1", port)) { Log($"Host 已在运行 ({port})"); return; }
        EnsureMySql();
        var dir = Path.Combine(_root, "Host", "src", "IndustrialIoT.Host");
        var dll = Path.Combine(dir, "bin", "Debug", "net8.0", "IndustrialIoT.Host.dll");
        if (!File.Exists(dll)) { Log($"未找到 {dll}，请先构建"); return; }
        Runner.StartWindow("dotnet", $"\"{dll}\" --urls http://localhost:{port} --environment Development", dir, Background);
        Log($"Host 启动: http://localhost:{port}");
    }

    private void StartGateway()
    {
        var port = _cfg.GetInt("运行 (Launcher)", "GatewayPort", 5087);
        if (Probe.TcpOpen("127.0.0.1", port)) { Log($"Gateway 已在运行 ({port})"); return; }
        EnsureMySql();
        var dir = Path.Combine(_root, "MachineConnectionApi20260423", "MachineConnectionApi", "MachineConnectionApi");
        var dll = Path.Combine(dir, "bin", "Debug", "net8.0", "MachineConnectionApi.dll");
        if (!File.Exists(dll)) { Log($"未找到 {dll}，请先构建"); return; }
        if (GatewayUseMock) PatchGatewayBaseUrl(dir);
        PatchGatewayVerifySimulate(dir);
        Runner.StartWindow("dotnet", $"\"{dll}\" --urls http://localhost:{port} --environment Development", dir, Background);
        Log($"Gateway 启动: http://localhost:{port}");
    }

    private void StartFrontend()
    {
        var port = _cfg.GetInt("Frontend (Vite)", "Port", 4171);
        if (Probe.TcpOpen("127.0.0.1", port)) { Log($"Frontend 已在运行 ({port})"); return; }
        var dir = FrontendDir();
        var vite = Path.Combine(dir, "node_modules", "vite", "bin", "vite.js");
        if (File.Exists(vite)) Runner.StartWindow("node", "node_modules/vite/bin/vite.js", dir, Background);
        else Runner.StartWindow("cmd.exe", "/c npx vite", dir, Background);
        Log($"Frontend 启动: http://localhost:{port}");
    }

    private void StopAll()
    {
        Runner.StopPort(MockPort, Log);
        Runner.StopPort(FrontendPort, Log);
        Runner.StopPort(GatewayPort, Log);
        Runner.StopPort(HostPort, Log);
        _status = Probe.Check(_cfg);
    }

    private void RunFull()
    {
        Log("== 全流程：修正 → 构建 → 启动 ==");
        StartAll();
        Log("== 全流程完成 ==");
    }

    private bool HandleLetter(ConsoleKey key)
    {
        switch (key)
        {
            case ConsoleKey.C: _status = Probe.Check(_cfg); Log("已刷新状态"); return true;
            case ConsoleKey.F: Ensure(); return true;
            case ConsoleKey.B: BuildAll(); return true;
            case ConsoleKey.S: StartAll(); return true;
            case ConsoleKey.X: StopAll(); return true;
            case ConsoleKey.R: RunFull(); return true;
            case ConsoleKey.D: Save(); Log("配置已保存"); return true;
            default: return false;
        }
    }

    private void MoveButton(int delta)
    {
        var count = Buttons().Count;
        _buttonIndex = (_buttonIndex + delta % count + count) % count;
    }

    /// <summary>触发按钮：先把控件切到“处理中”并立即重绘，再执行阻塞操作，最后再切一次状态。</summary>
    private void TriggerButton(int index)
    {
        var buttons = Buttons();
        if (index < 0 || index >= buttons.Count) return;
        var b = buttons[index];

        _buttonState[b.Id] = "…";
        Render(BuildRows());

        try
        {
            switch (b.Id)
            {
                case "check": _status = Probe.Check(_cfg); Log("已刷新状态"); break;
                case "fix": Ensure(); break;
                case "build": BuildAll(); break;
                case "runall": RunFull(); break;
                case "mysql": TogglePort("MySQL", MySqlPort, StartMySql); break;
                case "host": TogglePort("Host", HostPort, StartHost); break;
                case "gateway": TogglePort("Gateway", GatewayPort, StartGateway); break;
                case "frontend": TogglePort("前端", FrontendPort, StartFrontend); break;
                case "mock": TogglePort("Mock", MockPort, StartMock); break;
                case "influx": ToggleInflux(); break;
            }
            _buttonState[b.Id] = "✓";
        }
        catch (Exception ex)
        {
            Log($"按钮 [{b.Text}] 失败: {ex.Message}");
            _buttonState[b.Id] = "✗";
        }

        _status = Probe.Check(_cfg);
        Render(BuildRows());
    }

    private void TogglePort(string name, int port, Action start)
    {
        if (Probe.TcpOpen("127.0.0.1", port))
        {
            Log($"停止 {name}（端口 {port}）");
            Runner.StopPort(port, Log);
        }
        else
        {
            start();
        }
    }

    private void StartMySql()
    {
        if (SkipMySql) { Log("SkipMySql=on，跳过 MySQL"); return; }
        Log("== 启动/修正 MySQL ==");
        Runner.RunCaptured("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(_root, "deploy", "mysql", "Start-MySql.ps1")}\"", _root, Log);
    }

    private void StartMock()
    {
        var port = MockPort;
        if (Probe.TcpOpen("127.0.0.1", port)) { Log($"Mock 已在运行 ({port})"); return; }
        var dir = Path.Combine(_root, "tools", "IottDevMock");
        var dll = Path.Combine(dir, "bin", "Debug", "net8.0", "IottDevMock.dll");
        if (!File.Exists(dll)) { Log($"未找到 Mock（{dll}），已跳过；main 无 tools/IottDevMock，可沿用既有测试服务"); return; }
        Runner.StartWindow("dotnet", $"\"{dll}\" serve --port {port}", _root, Background);
        Log($"Mock 启动: http://localhost:{port}");
    }

    /// <summary>InfluxDB 按钮：8181 已监听则停容器，否则启动（必要时先拉起 Docker Desktop）。</summary>
    private void ToggleInflux()
    {
        if (Probe.TcpOpen("127.0.0.1", InfluxPort)) StopInflux();
        else StartInflux();
    }

    private void StartInflux()
    {
        if (Probe.TcpOpen("127.0.0.1", InfluxPort)) { Log($"InfluxDB 已在运行 ({InfluxPort})"); return; }
        Log("== 启动/修正 InfluxDB (Docker) ==");
        if (!EnsureDockerRunning()) { Log("Docker 引擎不可用，InfluxDB 未启动。"); return; }

        var script = InfluxStartScript;
        if (!File.Exists(script)) { Log($"未找到 InfluxDB 启动脚本：{script}"); return; }

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dockerDir = Path.GetDirectoryName(DockerExe);
        if (!string.IsNullOrEmpty(dockerDir) && Directory.Exists(dockerDir))
            env["PATH"] = dockerDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");

        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Port {InfluxPort} -BindAddress {InfluxBindAddress}";
        Runner.RunCaptured("powershell.exe", args, _root, Log, extraEnv: env.Count > 0 ? env : null);
    }

    private void StopInflux()
    {
        Log("== 停止 InfluxDB (Docker) ==");
        if (!Probe.DockerDaemonUp(DockerExe)) { Log("Docker 引擎未运行，无需停止。"); return; }
        var compose = InfluxComposeFile;
        if (!File.Exists(compose)) { Log($"未找到 compose 文件：{compose}"); return; }
        Runner.RunCaptured(DockerExe, $"compose -f \"{compose}\" stop", _root, Log);
    }

    /// <summary>确保 Docker 引擎可用：已就绪直接返回；未运行且允许时自动启动 Docker Desktop 并等待引擎就绪。</summary>
    private bool EnsureDockerRunning()
    {
        if (Probe.DockerDaemonUp(DockerExe)) return true;
        if (!AutoStartDockerDesktop)
        {
            Log("Docker 引擎未运行（AutoStartDockerDesktop=false）；请手动启动 Docker Desktop。");
            return false;
        }

        var desktop = DockerDesktopExe;
        if (string.IsNullOrWhiteSpace(desktop) || !File.Exists(desktop))
        {
            Log("未找到 Docker Desktop 可执行文件，无法自动启动；可在 [InfluxDB (Docker)] 配置 DockerDesktopExe。");
            return false;
        }

        Log($"Docker 引擎未运行，正在启动 Docker Desktop：{desktop}");
        try { Runner.StartWindow(desktop, "", Path.GetDirectoryName(desktop) ?? _root, background: false); }
        catch (Exception ex) { Log($"启动 Docker Desktop 失败：{ex.Message}"); return false; }

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(30, InfluxStartTimeout));
        while (DateTime.UtcNow < deadline)
        {
            if (Probe.DockerDaemonUp(DockerExe, 3000)) { Log("Docker 引擎已就绪"); return true; }
            System.Threading.Thread.Sleep(2000);
        }
        Log($"等待 Docker 引擎超时（{InfluxStartTimeout}s）；Docker Desktop 可能仍在启动，请稍后重试。");
        return false;
    }

    /// <summary>把网关 appsettings.Local.json 的 IndustrialIoT:BaseUrl 指向 Mock（会备份 .bak）。</summary>
    private void PatchGatewayBaseUrl(string gatewayDir)
    {
        var path = Path.Combine(gatewayDir, "appsettings.Local.json");
        var url = $"http://localhost:{MockPort}";
        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", true);
                root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject();
            }
            else root = new JsonObject();
            var iot = root["IndustrialIoT"] as JsonObject ?? new JsonObject();
            iot["BaseUrl"] = url;
            root["IndustrialIoT"] = iot;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Log($"Gateway BaseUrl 已指向 Mock: {url}（原文件备份为 .bak）");
        }
        catch (Exception ex) { Log($"改写 Gateway BaseUrl 失败: {ex.Message}"); }
    }

    /// <summary>把网关 appsettings.Local.json 的 Verify:Simulate 同步为启动器开关（会备份 .bak）。</summary>
    private void PatchGatewayVerifySimulate(string gatewayDir)
    {
        if (!VerifySimulateSupported) return;
        var path = Path.Combine(gatewayDir, "appsettings.Local.json");
        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", true);
                root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject();
            }
            else root = new JsonObject();
            var verify = root["Verify"] as JsonObject ?? new JsonObject();
            verify["Simulate"] = VerifySimulate;
            root["Verify"] = verify;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Log($"Gateway Verify:Simulate 已设为 {VerifySimulate.ToString().ToLowerInvariant()}（原文件备份为 .bak）");
        }
        catch (Exception ex) { Log($"改写 Gateway Verify:Simulate 失败: {ex.Message}"); }
    }

    private const int FooterSize = 8;
    // 头部高度随服务行数自适应：表头(1)+服务行+选项(1)+按钮两行(2)+缓冲(2)；
    // 原先固定 9 行，服务变多后会把第二行按钮挤出可视区，故按需增高。
    private int HeaderHeight() => Math.Clamp(_status.Count + 6, 10, 20);
    private int BodyHeight() => Math.Max(5, Console.WindowHeight - HeaderHeight() - FooterSize);

    private void Render(List<(bool Header, int Si, int Ei)> rows)
    {
        var bodyHeight = BodyHeight();
        if (_selected < _scroll) _scroll = _selected;
        if (_selected >= _scroll + bodyHeight) _scroll = _selected - bodyHeight + 1;
        _scroll = Math.Max(0, Math.Min(_scroll, Math.Max(0, rows.Count - bodyHeight)));

        var header = BuildHeader();
        var body = BuildBody(rows, bodyHeight);
        var footer = BuildFooter();

        var layout = new Layout("root").SplitRows(
            new Layout("header").Size(HeaderHeight()),
            new Layout("body"),
            new Layout("footer").Size(FooterSize));
        layout["header"].Update(header);
        layout["body"].Update(body);
        layout["footer"].Update(footer);

        try { AnsiConsole.Clear(); AnsiConsole.Write(layout); }
        catch { /* 终端尺寸异常时忽略 */ }
    }

    private IRenderable BuildHeader()
    {
        var table = new Table().Border(TableBorder.None).Expand();
        table.AddColumn("服务");
        table.AddColumn("状态");
        table.AddColumn("详情");
        table.AddColumn("地址");
        foreach (var s in _status)
            table.AddRow(
                Markup.Escape(s.Name),
                s.Up ? "[green]Running[/]" : "[grey]Stopped[/]",
                Markup.Escape(s.Detail),
                Markup.Escape(s.Endpoint));

        var toggles = $"[grey]选项:[/] SkipMySql=[blue]{(SkipMySql ? "on" : "off")}[/] " +
                      $"SkipFrontend=[blue]{(SkipFrontend ? "on" : "off")}[/] " +
                      $"NoBuild=[blue]{(NoBuild ? "on" : "off")}[/] " +
                      $"Background=[blue]{(Background ? "on" : "off")}[/] " +
                      $"UseMock=[blue]{(UseMock ? "on" : "off")}[/] " +
                      $"UseInflux=[blue]{(UseInflux ? "on" : "off")}[/] " +
                      $"VerifySim=[blue]{(VerifySimulate ? "on" : "off")}[/]  " +
                      $"[grey]根目录 {Markup.Escape(_root)}[/]";

        return new Rows(table, new Markup(toggles),
            ButtonRow(["check", "fix", "build", "runall"]),
            ButtonRow(["mysql", "influx", "host", "gateway", "frontend", "mock"]));
    }

    /// <summary>把一组按钮渲染成一行 Markup；聚焦的按钮高亮，服务按钮带运行状态点。</summary>
    private Markup ButtonRow(string[] ids)
    {
        var buttons = Buttons();
        var parts = new List<string>();
        foreach (var id in ids)
        {
            var idx = buttons.FindIndex(x => x.Id == id);
            var b = buttons[idx];
            _buttonState.TryGetValue(b.Id, out var state);
            var up = b.IsService && ServiceUp(b.ServiceName);
            var marker = state ?? (b.IsService ? (up ? "●" : "○") : "");
            var color = state == "…" ? "yellow"
                : state == "✓" ? "green"
                : state == "✗" ? "red"
                : b.IsService ? (up ? "green" : "grey")
                : "blue";
            var label = $"{b.Text}{marker}";
            parts.Add(_buttonsFocused && idx == _buttonIndex
                ? $"[black on deepskyblue1] {label} [/]"
                : $"[{color}] {label} [/]");
        }
        var prefix = _buttonsFocused ? "[white]▲ 按钮[/] " : "[grey]按钮:[/] ";
        return new Markup(prefix + string.Join("  ", parts));
    }

    private IRenderable BuildBody(List<(bool Header, int Si, int Ei)> rows, int bodyHeight)
    {
        var lines = new List<IRenderable>();
        var end = Math.Min(rows.Count, _scroll + bodyHeight);
        for (var i = _scroll; i < end; i++)
        {
            var r = rows[i];
            string text;
            Style style;
            if (r.Header)
            {
                var s = _cfg.Sections[r.Si];
                var arrow = s.Expanded ? "▼" : "▶";
                text = $"{arrow} {s.Name}  ({s.Items.Count})";
                style = new Style(foreground: Color.Yellow, decoration: Decoration.Bold);
            }
            else
            {
                var e = _cfg.Sections[r.Si].Items[r.Ei];
                text = $"    {e.Key} = {e.Value}";
                style = new Style(foreground: Color.Grey);
            }

            if (i == _selected)
            {
                style = new Style(foreground: Color.White, background: Color.Blue);
                text = "> " + text;
            }

            var t = new Text(text, style);
            t.Overflow = Overflow.Crop;
            lines.Add(t);
        }

        if (lines.Count == 0) lines.Add(new Text("(无配置项)", new Style(foreground: Color.Grey)));
        return new Rows(lines);
    }

    private IRenderable BuildFooter()
    {
        var hint = new Markup(
            "[grey]↑↓ 移动  Enter 编辑  ←→ 折叠  [white]Tab[/] 聚焦按钮(←→选择/Enter触发)  " +
            "[blue]C[/]检查 [blue]F[/]修正 [blue]B[/]构建 [blue]S[/]启动 [blue]X[/]停止 [blue]R[/]全流程 [blue]D[/]保存 [blue]Q[/]退出[/]");

        var tail = _log.Count <= 4 ? _log : _log.GetRange(_log.Count - 4, 4);
        var logLines = tail.Count == 0
            ? new Markup("[grey](无日志)[/]")
            : (IRenderable)new Rows(tail.Select(l => (IRenderable)new Text("  " + l, new Style(foreground: Color.Silver))));

        return new Panel(new Rows(hint, logLines)).Header("[bold]操作与日志[/]").Expand();
    }
}
