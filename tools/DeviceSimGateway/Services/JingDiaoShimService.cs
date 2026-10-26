namespace DeviceSimGateway.Services;

using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceSimGateway.Core;

/// <summary>北京精雕 JdMon **shim** 的 mock（HTTP）。Host 的 JingDiaoDriver 通过 `ShimBaseUri` 连 shim；
/// 本服务按其 HTTP 契约应答，从而无需原生 JdMon DLL 即可联调精雕协议。响应字段为 camelCase。</summary>
public sealed class JingDiaoShimService : IDeviceService
{
    public string Key => "jingdiao";
    public string Name => "JingDiao Shim";
    public string Description => "北京精雕 shim mock（HTTP /api/jingdiao/*）";
    public int DefaultPort => 7080;
    public bool Implemented => true;
    public ServiceState State { get; private set; } = ServiceState.Stopped;
    public string Message { get; private set; } = "";
    public int Port { get; private set; }

    private WebApplication? _app;
    private SimDataStore? _store;
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public async Task StartAsync(int port, SimDataStore data, GeneratorConfig gen, CancellationToken ct)
    {
        _store = data; Port = port;
        _files["/O0001.CNC"] = System.Text.Encoding.ASCII.GetBytes("(MOCK JD) G21 G90\nM03 S10000\nM30\n");

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        var app = builder.Build();

        app.MapGet("/health", () => Results.Json(new { status = "ok", service = "jingdiao", protocolVersion = 1, architecture = "x64", error = (string?)null }));

        var g = app.MapGroup("/api/jingdiao");
        g.MapPost("/connect", () => Ok(new { returnCode = 0, sessionId = "sess-1" }));
        g.MapPost("/disconnect", () => Ok(new { returnCode = 0 }));
        g.MapPost("/ping", () => Ok(new { returnCode = 0 }));
        g.MapPost("/get-mach-pos", () =>
        {
            var s = _store!; lock (s.Sync) return Ok(new { returnCode = 0, value = new { machine = new[] { R(s.Holding, 0) / 1000.0, R(s.Holding, 1) / 1000.0, R(s.Holding, 2) / 1000.0 }, absolute = new[] { R(s.Holding, 0) / 1000.0, R(s.Holding, 1) / 1000.0, R(s.Holding, 2) / 1000.0 }, relative = new[] { 0.0, 0.0, 0.0 } } });
        });
        g.MapPost("/get-basic-modal", () =>
        {
            var s = _store!; lock (s.Sync) return Ok(new { returnCode = 0, value = new { workCoordinate = 1, feedrate = (float)(R(s.Input, 1) / 10.0), spindleSpeed = R(s.Input, 0), toolNo = 1, machiningTimeMinutes = 0f, programNo = 1, mainProgramNo = 1 } });
        });
        g.MapPost("/get-prog-state", () => Ok(new { returnCode = 0, value = 3 }));
        g.MapPost("/get-alarm", () => Ok(new { returnCode = 0, value = 0 }));
        g.MapPost("/get-spindle", () =>
        {
            var s = _store!; lock (s.Sync) return Ok(new { returnCode = 0, value = new { current = (double)R(s.Input, 0) / 100.0, torque = 5.0, power = 12.0 } });
        });
        g.MapPost("/get-rate", () => Ok(new { returnCode = 0, value = new { spindle = 100, feed = 100 } }));
        g.MapPost("/get-macro", () => Ok(new { returnCode = 0, value = 0.0 }));
        g.MapPost("/set-macro", () => Ok(new { returnCode = 0 }));
        g.MapPost("/get-line-no", () => Ok(new { returnCode = 0, value = 10 }));
        g.MapPost("/get-part-count", () =>
        {
            var s = _store!; lock (s.Sync) return Ok(new { returnCode = 0, value = (int)R(s.Holding, 3) });
        });
        g.MapPost("/list-files", () =>
        {
            var items = new JsonArray();
            foreach (var kv in _files) items.Add(new JsonObject { ["path"] = kv.Key, ["name"] = kv.Key.TrimStart('/'), ["isDirectory"] = false, ["sizeBytes"] = kv.Value.Length });
            return Results.Content(new JsonObject { ["returnCode"] = 0, ["value"] = items }.ToJsonString(), "application/json");
        });
        g.MapPost("/send-nc-file", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            var file = form.Files.Count > 0 ? form.Files[0] : null;
            if (file is not null) { using var ms = new MemoryStream(); await file.CopyToAsync(ms); _files["/" + file.FileName] = ms.ToArray(); }
            return Ok(new { returnCode = 0 });
        });
        g.MapPost("/receive-file", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var path = doc.RootElement.TryGetProperty("remotePath", out var p) ? p.GetString() ?? "" : "";
            return _files.TryGetValue(path, out var b) ? Results.File(b, "application/octet-stream", Path.GetFileName(path)) : Results.NotFound();
        });
        g.MapPost("/delete-file", () => Ok(new { returnCode = 0 }));

        _app = app;
        await app.StartAsync(ct);
        State = ServiceState.Running;
        Message = $"监听 0.0.0.0:{port}（精雕 shim mock）";
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_app is not null) { try { await _app.StopAsync(TimeSpan.FromSeconds(3)); } catch { } try { await _app.DisposeAsync(); } catch { } _app = null; }
        if (State == ServiceState.Running) { State = ServiceState.Stopped; Message = "已停止"; }
    }

    private static IResult Ok(object o) => Results.Json(o);
    private static ushort R(ushort[] a, int i) => i >= 0 && i < a.Length ? a[i] : (ushort)0;
}
