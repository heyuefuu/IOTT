namespace DeviceSimGateway.Services;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DeviceSimGateway.Core;

/// <summary>广数 GSK WebServer(G3IOT) 模拟服务端：HTTP 静态/参数接口 + WebSocket 实时数据帧。
/// 端点与 JSON 字段严格对齐 Host 端 GskWebServerDriver 的解析约定。</summary>
public sealed class GskWebServerService : IDeviceService
{
    public string Key => "gskws";
    public string Name => "GSK WebServer";
    public string Description => "广数 G3IOT HTTP 服务端（/api/v1/{sn}/… + /ws/{sn} 实时帧）";
    public int DefaultPort => 11520;
    public bool Implemented => true;
    public ServiceState State { get; private set; } = ServiceState.Stopped;
    public string Message { get; private set; } = "";
    public int Port { get; private set; }

    private const string Sn = "cnc";
    private WebApplication? _app;
    private SimDataStore? _store;

    public async Task StartAsync(int port, SimDataStore data, GeneratorConfig gen, CancellationToken ct)
    {
        _store = data; Port = port;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        var app = builder.Build();
        app.UseWebSockets();

        var root = $"/api/v1/{Sn}";
        app.MapGet($"{root}/mc", () => Json(StaticMc()));
        app.MapGet($"{root}/macro-values", () => Json(Macros()));
        app.MapGet($"{root}/params", () => Json(Params()));
        app.MapGet($"{root}/info/diags", () => Json(Diags()));
        app.MapGet($"{root}/info/alarms", () => Json(Alarms()));
        app.MapGet($"{root}/info/history", () => Json(History()));
        app.MapGet($"{root}/mc/gcode", () => Json(ProgramList()));
        app.MapGet($"{root}/plc/regs", () => Json(PlcRegs()));
        app.MapGet($"{root}/tool-offsets", () => Json(ToolOffsets()));
        app.MapGet($"{root}/tool-lifes", () => Json(Array.Empty<object>()));

        // 实时 WebSocket：/ws/{sn}
        app.Map("/ws/{sn}", async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var ct2 = ctx.RequestAborted;
            try
            {
                while (ws.State == WebSocketState.Open && !ct2.IsCancellationRequested)
                {
                    var frame = RealtimeFrame();
                    var bytes = Encoding.UTF8.GetBytes(frame);
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct2);
                    await Task.Delay(500, ct2);
                }
            }
            catch { /* 断开 */ }
            finally { try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { } }
        });

        _app = app;
        await app.StartAsync(ct);
        State = ServiceState.Running;
        Message = $"监听 0.0.0.0:{port}（{root}/mc、/ws/{Sn}）";
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_app is not null)
        {
            try { await _app.StopAsync(TimeSpan.FromSeconds(3)); } catch { }
            try { await _app.DisposeAsync(); } catch { }
            _app = null;
        }
        if (State == ServiceState.Running) { State = ServiceState.Stopped; Message = "已停止"; }
    }

    private static IResult Json(object o) => Results.Json(o);
    private static ushort Reg(ushort[] a, int i) => i >= 0 && i < a.Length ? a[i] : (ushort)0;

    private object StaticMc() => new
    {
        model = "GSK988TD",
        pathcount = 1,
        axes = new[]
        {
            new
            {
                totalcount = 3,
                names = new { absolute = new[] { "X", "Z", "C" } },
                @decimal = new { absolute = new[] { 3, 3, 3 } },
                unit = new { absolute = new[] { "mm", "mm", "deg" } },
            }
        },
        spindle = new[] { new { totalcount = 1 } },
    };

    private object Macros()
    {
        var s = _store!;
        lock (s.Sync) return new { data = new[] { new { index = 0, value = (int)Reg(s.Holding, 0) }, new { index = 1, value = (int)Reg(s.Holding, 1) } } };
    }

    private object Params()
    {
        var s = _store!;
        lock (s.Sync) return new { data = new[] { new { no = 100, value = (int)Reg(s.Holding, 2) } } };
    }

    private object Diags()
    {
        var s = _store!;
        lock (s.Sync) return new { data = new[] { new { no = 1, value = (int)Reg(s.Input, 2) } } };
    }

    private object Alarms() => new { data = Array.Empty<object>() };

    private object History() => new { data = Array.Empty<object>() };

    private object ProgramList() => new { data = new[] { "O0001.CNC", "O0002.CNC" } };

    private object PlcRegs()
    {
        var s = _store!;
        lock (s.Sync) return new { data = new[] { new { addr = 0, value = (int)Reg(s.Input, 3) } } };
    }

    private object ToolOffsets()
    {
        var s = _store!;
        lock (s.Sync) return new { value = new[] { new { axis = new[] { new[] { Reg(s.Holding, 4) / 1000.0, 0.0 }, new double[] { } }, r = new double[] { 0, 0 }, t = 0 } } };
    }

    /// <summary>实时帧：字段名对齐 GskWebServerDriver 的 TryGetRealtimeFieldValue。</summary>
    private string RealtimeFrame()
    {
        var s = _store!;
        ushort[] h, inp; bool running;
        lock (s.Sync) { h = (ushort[])s.Holding.Clone(); inp = (ushort[])s.Input.Clone(); }
        running = true;
        var frame = new
        {
            mode = 1,
            state = running ? 2 : 1,               // state==2 → Running=true
            cord = new { absolute = new[] { (double)Reg(h, 0) / 100, (double)Reg(h, 1) / 100, (double)Reg(h, 2) / 100 }, machine = new[] { 0.0, 0.0, 0.0 }, relative = new[] { 0.0, 0.0, 0.0 } },
            speed = new { actual = (double)Reg(inp, 1) / 10, programe = (double)Reg(inp, 1) / 10 },
            spindle = new { actspeed = new[] { (double)Reg(inp, 0) }, cmdspeed = new[] { (double)Reg(inp, 0) } },
            @override = new { feed = 100, spindle = 100, rappid = 100, jog = 100, mpg = 100 },
            @event = new { esp = 0, alm = 0 },
            parts = new { target = 10, cutted = (int)Reg(h, 3) },
            time = new { run = 0, cut = 0 },
            tool = new { toolno = (int)Reg(h, 5), offsetno = 0 },
            gcode = new { name = "O0001", line = (int)Reg(h, 6) },
        };
        return JsonSerializer.Serialize(frame);
    }
}
