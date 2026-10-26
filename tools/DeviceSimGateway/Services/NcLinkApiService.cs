namespace DeviceSimGateway.Services;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceSimGateway.Core;

/// <summary>NC-Link API Server（华中 NCLinkApi，HTTP）模拟：/v1/{device_id}/data/（get_value/get_keys/set_value…）、
/// /v1/{device_id}/file/（GET 下载 / POST 上传）、/api/tools/check-device。响应 {status:SUCCESS,code:0,value}</summary>
public sealed class NcLinkApiService : IDeviceService
{
    public string Key => "ncLinkApi";
    public string Name => "NC-Link API (华中)";
    public string Description => "NC-Link API Server（HTTP /v1/{deviceId}/data|file/）";
    public int DefaultPort => 19001;
    public bool Implemented => true;
    public ServiceState State { get; private set; } = ServiceState.Stopped;
    public string Message { get; private set; } = "";
    public int Port { get; private set; }

    private WebApplication? _app;
    private SimDataStore? _store;
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public async Task StartAsync(int port, SimDataStore data, GeneratorConfig gen, CancellationToken ct)
    {
        _store = data; Port = port;
        _files["O0001"] = Encoding.ASCII.GetBytes("(MOCK HNC) G21 G90\nM03 S1500\nG0 X0 Z0\nM30\n");
        _files["O0002"] = Encoding.ASCII.GetBytes("(MOCK HNC) M30\n");

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        var app = builder.Build();

        app.MapPost("/v1/{deviceId}/data/", async (string deviceId, HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var op = root.TryGetProperty("operation", out var o) ? o.GetString() ?? "" : "";
            var items = root.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array ? it : default;
            var outer = new JsonArray();
            if (items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var path = item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                    outer.Add(HandleItem(op, path, item));
                }
            }
            var resp = new JsonObject { ["status"] = "SUCCESS", ["code"] = 0, ["value"] = outer };
            return Results.Content(resp.ToJsonString(), "application/json; charset=utf-8");
        });

        app.MapGet("/v1/{deviceId}/file/", (string deviceId, string? key) =>
        {
            key ??= "";
            return _files.TryGetValue(key, out var b)
                ? Results.Bytes(b, "application/octet-stream")
                : Results.NotFound();
        });
        app.MapPost("/v1/{deviceId}/file/", async (string deviceId, HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            var key = form["key"].ToString();
            if (string.IsNullOrEmpty(key)) key = "uploaded";
            var file = form.Files.Count > 0 ? form.Files[0] : null;
            if (file is not null) { using var ms = new MemoryStream(); await file.CopyToAsync(ms); _files[key] = ms.ToArray(); }
            else _files[key] = Array.Empty<byte>();
            return Results.Content(new JsonObject { ["status"] = "SUCCESS", ["code"] = 0, ["value"] = new JsonArray(new JsonArray(true)) }.ToJsonString(), "application/json; charset=utf-8");
        });

        app.MapGet("/api/tools/check-device", (string? deviceId) => Results.Ok(new { deviceId, status = "SUCCESS" }));

        _app = app;
        await app.StartAsync(ct);
        State = ServiceState.Running;
        Message = $"监听 0.0.0.0:{port}（/v1/{{deviceId}}/data|file/）";
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

    private JsonArray HandleItem(string op, string path, JsonElement item)
    {
        var s = _store!;
        lock (s.Sync)
        {
            switch (op)
            {
                case "get_value":
                    return new JsonArray(ValueOf(path, item));
                case "get_length":
                    return new JsonArray(1000);
                case "get_keys":
                    return new JsonArray(_files.Keys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray());
                case "get_attributes":
                    return new JsonArray(new JsonObject { ["size"] = 0 });
                default: // set_value / add / delete
                    return new JsonArray(true);
            }
        }
    }

    private JsonNode? ValueOf(string path, JsonElement item)
    {
        var s = _store!;
        int idx = 0;
        if (item.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number) idx = ix.GetInt32();
        return path.ToUpperInvariant() switch
        {
            "/MACHINE/STATUS" => 1,
            "/MACHINE/FEED_SPEED" => Math.Round(Reg(s.Input, 1) / 10.0, 2),
            "/MACHINE/FEED_OVERRIDE" => 100,
            "/MACHINE/SPDL_OVERRIDE" or "/MACHINE/SPINDLE_OVERRIDE" => 100,
            "/MACHINE/PART_COUNT" => (int)Reg(s.Holding, 3),
            "/MACHINE/PROGRAM_NUMBER" => "O0001",
            "/MACHINE/CONTROLLER/VARIABLE@AXIS_0" => Math.Round(Reg(s.Holding, Math.Clamp(idx, 0, 2)) / 100.0, 3),
            "/MACHINE/CONTROLLER/VARIABLE@SYS" => idx == 0 ? 1 : 0,
            _ when path.ToUpperInvariant().StartsWith("/MACHINE/CONTROLLER/VARIABLE@REG") => (int)Reg(s.Holding, Math.Clamp(idx, 0, s.Holding.Length - 1)),
            _ => 0,
        };
    }

    private static ushort Reg(ushort[] a, int i) => i >= 0 && i < a.Length ? a[i] : (ushort)0;
}
