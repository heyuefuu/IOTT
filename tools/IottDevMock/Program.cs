using System.Text.Json;
using System.Text.Json.Serialization;
using IottDevMock.Mock;
using IottDevMock.Seed;

// IottDevMock —— 虚拟数据项目（阶段 0/1）
//   serve  启动 Mock 上游 Host（实现 IndustrialIoT.Host 被用到的 HTTP 契约）
//   seed   生成网关 App_Data/devices.json 等种子
//   clean  删除生成的种子
const string HelpText = """
IottDevMock — 虚拟数据项目

  serve [--port 5199] [--scenario Normal] [--seed 42] [--devices 30]
      启动 Mock 上游 Host（实现 Host 被用到的 HTTP 契约）。

  seed [--root <仓库根>] [--dry-run] [--verify] [--large]
      生成网关 App_Data/devices.json（Mock 设备台账）。
      --verify 追加 verify-tasks.json / 指标补丁 / 测试附件 / cs-connectivity.json
      --large  附件额外生成 200MiB（file-size 指标需要）

  clean [--root <仓库根>]
      删除生成的种子文件。

  simulate [--modbus-port 502] [--fins-port 9600] [--seed 42]
      启动协议端点模拟：Modbus TCP 从站 + FINS/TCP 从站（方案 C，供真实 Host 驱动直连）。

场景: Normal | HighLatency | IntermittentFault | DataDrift | PartialBad
""";

var cmd = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "serve";
var opt = ArgMap.Parse(args);

switch (cmd)
{
    case "serve":
        await ServeAsync(opt);
        return 0;
    case "seed":
        return SeedWriter.Run(opt);
    case "clean":
        return SeedWriter.Clean(opt);
    case "simulate":
        return await SimulateAsync(opt);
    case "help":
    case "--help":
    case "-h":
        Console.WriteLine(HelpText);
        return 0;
    default:
        Console.Error.WriteLine($"未知命令: {cmd}\n{HelpText}");
        return 1;
}

static async Task ServeAsync(ArgMap opt)
{
    var port = opt.GetInt("port", 5199);
    var scenario = opt.Get("scenario", "Normal");
    var seed = opt.GetInt("seed", 42);
    var count = opt.GetInt("devices", 30);

    var catalog = Catalog.Build(seed, count, scenario);
    var api = new HostApi(catalog, scenario, seed);

    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls($"http://localhost:{port}");
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

    var app = builder.Build();
    api.Map(app);

    Console.WriteLine($"IottDevMock listening on http://localhost:{port}");
    Console.WriteLine($"  scenario={scenario}  seed={seed}  devices={catalog.Count}");
    Console.WriteLine($"  将网关 IndustrialIoT:BaseUrl 指向 http://localhost:{port}");
    await app.RunAsync();
}

static async Task<int> SimulateAsync(ArgMap opt)
{
    var modbusPort = opt.GetInt("modbus-port", 502);
    var finsPort = opt.GetInt("fins-port", 9600);
    var seed = opt.GetInt("seed", 42);

    var modbus = new ModbusTcpSlave(modbusPort, seed);
    var fins = new FinsTcpServer(finsPort, seed);

    Console.WriteLine($"Modbus TCP 从站 0.0.0.0:{modbusPort}（ModbusTCP / ModbusRTU-over-TCP）");
    Console.WriteLine($"FINS/TCP 从站   0.0.0.0:{finsPort}（FINS，DM/CIO/WR/HR/AR 字读写）");
    Console.WriteLine("  将设备 host 指向 127.0.0.1、port 指向对应端口，用真实 IndustrialIoT.Host 驱动连接。");
    Console.WriteLine("  按 Ctrl+C 停止。");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    await Task.WhenAll(modbus.RunAsync(cts.Token), fins.RunAsync(cts.Token));
    return 0;
}

/// <summary>极简命令行参数解析：--key value / --flag。</summary>
public sealed class ArgMap
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public static ArgMap Parse(string[] args)
    {
        var map = new ArgMap();
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--")) continue;
            var key = token[2..];
            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { value = args[i + 1]; i++; }
            map._values[key] = value;
        }
        return map;
    }

    public string Get(string key, string fallback) =>
        _values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v! : fallback;

    public int GetInt(string key, int fallback) => int.TryParse(Get(key, ""), out var v) ? v : fallback;

    public bool Has(string key) => _values.ContainsKey(key);
}
