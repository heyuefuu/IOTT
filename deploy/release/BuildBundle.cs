// 单文件脚本（.NET 10 file-based app）：
//   将 Host / Gateway / (Mock) / 前端 编译发布到同一个目标文件夹，便于整包拷贝到目标机。
// 运行：
//   dotnet run .\deploy\release\BuildBundle.cs -- --clean
//   dotnet run .\deploy\release\BuildBundle.cs -- --runtime win-x64 --include-mock
//   dotnet run .\deploy\release\BuildBundle.cs -- --root D:\Company\IOTT\IOTT --out D:\bundle
// 依赖：dotnet SDK、Node/npm（构建前端时）。

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

Console.OutputEncoding = Encoding.UTF8;
return BuildBundle.Run(args);

static class BuildBundle
{
    static readonly UTF8Encoding Utf8NoBom = new(false);

    public static int Run(string[] args)
    {
        if (Has(args, "--help") || Has(args, "-h")) { Help(); return 0; }

        string root;
        try { root = ResolveRoot(ValueAfter(args, "--root")); }
        catch (Exception ex) { Console.Error.WriteLine("[错误] " + ex.Message); return 2; }

        var configuration = ValueAfter(args, "--configuration") ?? "Release";
        var runtime = ValueAfter(args, "--runtime");
        var outDir = ValueAfter(args, "--out") ?? Path.Combine(root, "artifacts", "iott-bundle");
        outDir = Path.GetFullPath(outDir);
        var clean = Has(args, "--clean");
        var skipFrontend = Has(args, "--skip-frontend");
        var includeMock = Has(args, "--include-mock");
        var includeInflux = Has(args, "--include-influx");

        Console.WriteLine($"仓库根：{root}");
        Console.WriteLine($"输出目录：{outDir}");
        Console.WriteLine($"配置：{configuration}{(runtime is null ? "" : $"  运行时：{runtime}(自包含)")}");

        try
        {
            PrepareOut(outDir, clean);
            var comps = new List<string> { "host", "gateway" };

            Publish("Host", Path.Combine(root, "Host", "src", "IndustrialIoT.Host", "IndustrialIoT.Host.csproj"),
                Path.Combine(outDir, "host"), configuration, runtime);
            Publish("Gateway", Path.Combine(root, "MachineConnectionApi20260423", "MachineConnectionApi", "MachineConnectionApi", "MachineConnectionApi.csproj"),
                Path.Combine(outDir, "gateway"), configuration, runtime);
            if (includeMock)
            {
                Publish("Mock", Path.Combine(root, "tools", "IottDevMock", "IottDevMock.csproj"),
                    Path.Combine(outDir, "mock"), configuration, runtime);
                comps.Add("mock");
            }

            if (!skipFrontend)
            {
                BuildFrontend(root, outDir);
                comps.Add("frontend");
            }
            else Console.WriteLine("\n[跳过] 前端（--skip-frontend）");

            CopyDeployAssets(root, outDir, includeInflux, includeMock);
            PrepareRuntimeData(outDir);
            WriteManifest(outDir, configuration, runtime, comps);

            var sizeMb = Math.Round(SumSizeMb(outDir), 1);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n打包完成：{outDir}（约 {sizeMb} MB，组件：{string.Join(", ", comps)}）");
            Console.WriteLine("下一步：整包拷贝到目标机，运行  dotnet run .\\InstallRun.cs -- --action install");
            Console.ResetColor();
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("\n[打包失败] " + ex.Message);
            Console.ResetColor();
            return 1;
        }
    }

    static void Publish(string name, string proj, string dest, string configuration, string? runtime)
    {
        Console.WriteLine($"\n==== 发布 {name} -> {dest} ====");
        if (!File.Exists(proj)) throw new FileNotFoundException($"找不到项目：{proj}");
        Directory.CreateDirectory(dest);
        var cmd = $"publish \"{proj}\" -c {configuration} -o \"{dest}\" --nologo -v minimal";
        if (!string.IsNullOrWhiteSpace(runtime)) cmd += $" -r {runtime} --self-contained true";
        if (RunInherited("dotnet", cmd, Path.GetDirectoryName(proj)!) != 0)
            throw new InvalidOperationException($"{name} 发布失败（dotnet publish 非 0 退出）");
    }

    static void BuildFrontend(string root, string outDir)
    {
        Console.WriteLine("\n==== 构建前端 (Vite) ====");
        var feDir = Path.Combine(root, "code20260423", "code");
        if (!File.Exists(Path.Combine(feDir, "package.json")))
            throw new FileNotFoundException($"未找到前端工程：{feDir}");

        var installCmd = File.Exists(Path.Combine(feDir, "package-lock.json")) ? "npm ci" : "npm install";
        if (RunShell(installCmd, feDir) != 0) throw new InvalidOperationException("npm 安装依赖失败");
        if (RunShell("npm run build", feDir) != 0) throw new InvalidOperationException("前端构建失败");

        var feDest = Path.Combine(outDir, "frontend");
        Directory.CreateDirectory(feDest);
        CopyDir(Path.Combine(feDir, "dist"), Path.Combine(feDest, "dist"));
        File.WriteAllText(Path.Combine(feDest, "serve.mjs"), ServeMjs, Utf8NoBom);
    }

    static void CopyDeployAssets(string root, string outDir, bool includeInflux, bool includeMock)
    {
        Console.WriteLine("\n==== 复制部署资产 ====");
        var dest = Path.Combine(outDir, "deploy");
        Directory.CreateDirectory(dest);
        var mysql = Path.Combine(root, "deploy", "mysql");
        if (Directory.Exists(mysql)) CopyDir(mysql, Path.Combine(dest, "mysql"));
        if (includeInflux)
        {
            var influx = Path.Combine(root, "deploy", "influxdb");
            if (Directory.Exists(influx)) CopyDir(influx, Path.Combine(dest, "influxdb"));
        }
        // 把安装脚本一并放进包内
        var installer = Path.Combine(root, "deploy", "release", "InstallRun.cs");
        if (File.Exists(installer)) File.Copy(installer, Path.Combine(outDir, "InstallRun.cs"), true);
    }

    static void PrepareOut(string outDir, bool clean)
    {
        if (Directory.Exists(outDir))
        {
            if (clean) Directory.Delete(outDir, true);
            else Console.WriteLine("[提示] 输出目录已存在，将覆盖（加 --clean 可先清空）。");
        }
        Directory.CreateDirectory(outDir);
    }

    static void PrepareRuntimeData(string outDir)
    {
        // 网关/Host 以 AppContext.BaseDirectory\App_Data 为运行数据目录；预建目录避免首次写入异常。
        Directory.CreateDirectory(Path.Combine(outDir, "gateway", "App_Data"));
        Directory.CreateDirectory(Path.Combine(outDir, "host", "App_Data"));
        Directory.CreateDirectory(Path.Combine(outDir, "logs"));
        Directory.CreateDirectory(Path.Combine(outDir, "run"));
    }

    static void WriteManifest(string outDir, string configuration, string? runtime, List<string> comps)
    {
        var manifest = new JsonObject
        {
            ["builtAt"] = DateTimeOffset.Now.ToString("s"),
            ["configuration"] = configuration,
            ["runtime"] = runtime,
            ["components"] = new JsonArray(comps.Select(c => (JsonNode)c).ToArray()),
            ["ports"] = new JsonObject
            {
                ["host"] = 5173, ["gateway"] = 5087, ["frontend"] = 4171, ["mysql"] = 3307, ["influx"] = 8181,
            },
            ["notes"] = "目标机运行 InstallRun.cs；前端由 frontend/serve.mjs(纯 Node，零依赖) 托管并代理到 gateway/host",
        };
        File.WriteAllText(Path.Combine(outDir, "manifest.json"), manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);
    }

    // ---------- 工具 ----------

    static int RunInherited(string exe, string arguments, string workDir)
    {
        Console.WriteLine($"$ {exe} {arguments}");
        var psi = new ProcessStartInfo(exe, arguments) { WorkingDirectory = workDir, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    static int RunShell(string commandLine, string workDir) => RunInherited("cmd.exe", "/c " + commandLine, workDir);

    static void CopyDir(string source, string dest)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)), true);
    }

    static double SumSizeMb(string dir) =>
        !Directory.Exists(dir) ? 0 : Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024d / 1024d;

    static bool Has(string[] args, string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    static string? ValueAfter(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    static string ResolveRoot(string? hint)
    {
        if (!string.IsNullOrWhiteSpace(hint))
        {
            var full = Path.GetFullPath(hint!);
            if (File.Exists(Path.Combine(full, "Host", "IndustrialIoT.Host.sln")) || File.Exists(Path.Combine(full, "Host", "src", "IndustrialIoT.Host", "IndustrialIoT.Host.csproj")))
                return full;
            throw new DirectoryNotFoundException($"指定目录不像仓库根（缺少 Host/）：{full}");
        }
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var cur = start;
            while (!string.IsNullOrEmpty(cur))
            {
                if (File.Exists(Path.Combine(cur, "Host", "src", "IndustrialIoT.Host", "IndustrialIoT.Host.csproj"))) return cur;
                var parent = Path.GetDirectoryName(cur);
                if (parent == cur) break;
                cur = parent;
            }
        }
        throw new DirectoryNotFoundException("未找到仓库根（应包含 Host/src/IndustrialIoT.Host）。用 --root <路径> 指定。");
    }

    static void Help()
    {
        Console.WriteLine("BuildBundle.cs — 将各项目编译发布到一个目标文件夹");
        Console.WriteLine("  --root <路径>        仓库根（默认自动向上查找 Host/src/IndustrialIoT.Host）");
        Console.WriteLine("  --out <路径>         输出目录（默认 <root>/artifacts/iott-bundle）");
        Console.WriteLine("  --configuration <c>  发布配置（默认 Release）");
        Console.WriteLine("  --runtime <rid>      自包含发布（win-x64 / linux-x64）；省略则框架依赖");
        Console.WriteLine("  --clean              先清空输出目录");
        Console.WriteLine("  --skip-frontend      不构建前端");
        Console.WriteLine("  --include-mock       一并发布 IottDevMock");
        Console.WriteLine("  --include-influx     复制 deploy/influxdb 资产");
    }

    // 零依赖 Node 静态服务器 + 反向代理（供目标机托管前端 dist）。
    const string ServeMjs = """
// 纯 Node（内置模块）静态文件服务器 + 反向代理；无需 npm 依赖。
// 环境变量：FRONTEND_PORT(默认 4171) GATEWAY_TARGET HOST_TARGET
import http from 'node:http';
import net from 'node:net';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const DIST = path.join(__dirname, 'dist');
const PORT = Number(process.env.FRONTEND_PORT || 4171);
const ROUTES = [
  { prefix: '/machine-connection', target: process.env.GATEWAY_TARGET || 'http://localhost:5087', ws: false },
  { prefix: '/industrial-iot', target: process.env.HOST_TARGET || 'http://localhost:5173', ws: true },
];
const MIME = { '.html':'text/html; charset=utf-8', '.js':'text/javascript', '.mjs':'text/javascript', '.css':'text/css', '.json':'application/json', '.svg':'image/svg+xml', '.png':'image/png', '.jpg':'image/jpeg', '.jpeg':'image/jpeg', '.gif':'image/gif', '.ico':'image/x-icon', '.woff':'font/woff', '.woff2':'font/woff2', '.map':'application/json', '.txt':'text/plain' };
const routeOf = (u) => ROUTES.find(r => u.startsWith(r.prefix));
function target(t) { const u = new URL(t); return { host: u.hostname, port: Number(u.port || 80) }; }

const server = http.createServer((req, res) => {
  const route = routeOf(req.url);
  if (route) {
    const t = target(route.target);
    const p = req.url.slice(route.prefix.length) || '/';
    const headers = { ...req.headers, host: t.host + ':' + t.port };
    const pr = http.request({ host: t.host, port: t.port, path: p, method: req.method, headers }, (up) => {
      res.writeHead(up.statusCode, up.headers); up.pipe(res);
    });
    pr.on('error', (e) => { res.writeHead(502); res.end('proxy error: ' + e.message); });
    req.pipe(pr);
    return;
  }
  let fp = path.join(DIST, decodeURIComponent((req.url || '/').split('?')[0]));
  if (!fp.startsWith(DIST)) { res.writeHead(403); res.end('forbidden'); return; }
  fs.stat(fp, (err, st) => {
    if (err || st.isDirectory()) fp = path.join(DIST, 'index.html');
    fs.readFile(fp, (e, data) => {
      if (e) { res.writeHead(404); res.end('not found'); return; }
      res.writeHead(200, { 'content-type': MIME[path.extname(fp).toLowerCase()] || 'application/octet-stream' });
      res.end(data);
    });
  });
});

server.on('upgrade', (req, socket, head) => {
  const route = routeOf(req.url);
  if (!route || !route.ws) { socket.destroy(); return; }
  const t = target(route.target);
  const p = req.url.slice(route.prefix.length) || '/';
  const up = net.connect(t.port, t.host, () => {
    const lines = Object.entries({ ...req.headers, host: t.host + ':' + t.port }).map(([k, v]) => k + ': ' + v).join('\r\n');
    up.write(req.method + ' ' + p + ' HTTP/1.1\r\n' + lines + '\r\n\r\n');
    if (head && head.length) up.write(head);
    socket.pipe(up); up.pipe(socket);
  });
  up.on('error', () => socket.destroy());
});

server.listen(PORT, () => console.log('frontend on http://0.0.0.0:' + PORT + '  proxy -> ' + ROUTES.map(r => r.prefix + '=' + r.target).join(', ')));
""";
}
