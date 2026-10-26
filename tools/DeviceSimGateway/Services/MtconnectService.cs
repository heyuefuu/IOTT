namespace DeviceSimGateway.Services;

using System.Globalization;
using System.Text;
using DeviceSimGateway.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>严格按 MTConnect 1.3 实现只读 Agent：/probe（设备树）+ /current（快照）。</summary>
public sealed class MtconnectService : IDeviceService
{
    private const string NsDevices = "urn:mtconnect.org:MTConnectDevices:1.3";
    private const string NsStreams = "urn:mtconnect.org:MTConnectStreams:1.3";
    private const string DeviceName = "SIM-CNC-01";
    private const string DeviceUuid = "SIM-CNC-01";

    public string Key => "mtconnect";
    public string Name => "MTConnect Agent";
    public string Description => "MTConnect 1.3 Agent（/probe、/current，只读）";
    public int DefaultPort => 5000;
    public bool Implemented => true;
    public ServiceState State { get; private set; } = ServiceState.Stopped;
    public string Message { get; private set; } = "";
    public int Port { get; private set; }

    private WebApplication? _app;
    private SimDataStore? _store;

    public async Task StartAsync(int port, SimDataStore data, GeneratorConfig gen, CancellationToken ct)
    {
        _store = data; Port = port;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        var app = builder.Build();
        app.MapGet("/", () => Results.Json(new { agent = "DeviceSimGateway-MTConnect", version = "1.3", device = DeviceName }));
        app.MapGet("/probe", () => _store!.Failure.ShouldFail() ? Results.StatusCode(503) : Results.Content(BuildProbe(), "application/xml", Encoding.UTF8));
        app.MapGet("/current", () => _store!.Failure.ShouldFail() ? Results.StatusCode(503) : Results.Content(BuildCurrent(), "application/xml", Encoding.UTF8));
        _app = app;
        await app.StartAsync(ct);
        State = ServiceState.Running;
        Message = $"监听 0.0.0.0:{port}（/probe, /current）";
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

    private string BuildProbe()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append($"<MTConnectDevices xmlns=\"{NsDevices}\" version=\"1.3\">");
        sb.Append("<Header><AssetCount>0</AssetCount></Header>");
        sb.Append("<Devices>");
        sb.Append($"<Device id=\"{DeviceName}\" name=\"{DeviceName}\" uuid=\"{DeviceUuid}\" iso841Class=\"3\">");
        sb.Append("<DataItems>");
        sb.Append("<DataItem id=\"avail\" category=\"EVENT\" type=\"AVAILABILITY\" name=\"avail\"/>");
        sb.Append("<DataItem id=\"exec\" category=\"EVENT\" type=\"EXECUTION\" name=\"exec\"/>");
        sb.Append("<DataItem id=\"program\" category=\"EVENT\" type=\"PROGRAM\" name=\"program\"/>");
        sb.Append("<DataItem id=\"xpos\" category=\"SAMPLE\" type=\"POSITION\" subType=\"ACTUAL\" units=\"MILLIMETER\" name=\"X\"/>");
        sb.Append("<DataItem id=\"ypos\" category=\"SAMPLE\" type=\"POSITION\" subType=\"ACTUAL\" units=\"MILLIMETER\" name=\"Y\"/>");
        sb.Append("<DataItem id=\"zpos\" category=\"SAMPLE\" type=\"POSITION\" subType=\"ACTUAL\" units=\"MILLIMETER\" name=\"Z\"/>");
        sb.Append("<DataItem id=\"spindle\" category=\"SAMPLE\" type=\"ROTARY_VELOCITY\" units=\"REVOLUTION/MINUTE\" name=\"S\"/>");
        sb.Append("<DataItem id=\"feed\" category=\"SAMPLE\" type=\"PATH_FEEDRATE\" subType=\"ACTUAL\" units=\"MILLIMETER/SECOND\" name=\"F\"/>");
        sb.Append("<DataItem id=\"partcount\" category=\"SAMPLE\" type=\"PART_COUNT\" name=\"partcount\"/>");
        sb.Append("</DataItems>");
        sb.Append("</Device>");
        sb.Append("</Devices></MTConnectDevices>");
        return sb.ToString();
    }

    private string BuildCurrent()
    {
        var store = _store!;
        ushort[] h, inp;
        lock (store.Sync) { h = (ushort[])store.Holding.Clone(); inp = (ushort[])store.Input.Clone(); }
        double x = Reg(h, 0) / 100.0, y = Reg(h, 1) / 100.0, z = Reg(h, 2) / 100.0;
        double s = Reg(inp, 0), f = Reg(inp, 1) / 10.0;
        int parts = Reg(h, 3);
        string seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append($"<MTConnectStreams xmlns=\"{NsStreams}\" version=\"1.3\">");
        sb.Append($"<Header><InstanceId>0</InstanceId><BufferSize>131072</BufferSize><nextSequenceNumber>1</nextSequenceNumber><lastSequenceNumber>1</lastSequenceNumber><firstSequenceNumber>1</firstSequenceNumber><lastUpdateTime>{DateTimeOffset.UtcNow:o}</lastUpdateTime></Header>");
        sb.Append("<Streams>");
        sb.Append($"<DeviceStream name=\"{DeviceName}\" uuid=\"{DeviceUuid}\">");
        sb.Append("<ComponentStream component=\"Controller\" name=\"controller\" componentId=\"controller\">");
        sb.Append($"<Events><Availability dataItemId=\"avail\" name=\"avail\" sequence=\"1\" timestamp=\"{DateTimeOffset.UtcNow:o}\">AVAILABLE</Availability>");
        sb.Append($"<Execution dataItemId=\"exec\" name=\"exec\" sequence=\"1\" timestamp=\"{DateTimeOffset.UtcNow:o}\">ACTIVE</Execution>");
        sb.Append($"<Program dataItemId=\"program\" name=\"program\" sequence=\"1\" timestamp=\"{DateTimeOffset.UtcNow:o}\">O0001</Program></Events>");
        sb.Append("</ComponentStream>");
        sb.Append("<ComponentStream component=\"Linear\" name=\"X\" componentId=\"x\"><Samples>");
        sb.Append($"<Position dataItemId=\"xpos\" name=\"X\" sequence=\"1\" timestamp=\"{DateTimeOffset.UtcNow:o}\">{x.ToString("F3", CultureInfo.InvariantCulture)}</Position></Samples></ComponentStream>");
        sb.Append("<ComponentStream component=\"Linear\" name=\"Y\" componentId=\"y\"><Samples>");
        sb.Append($"<Position dataItemId=\"ypos\" name=\"Y\" sequence=\"1\" timestamp=\"{DateTimeOffset.UtcNow:o}\">{y.ToString("F3", CultureInfo.InvariantCulture)}</Position></Samples></ComponentStream>");
        sb.Append("<ComponentStream component=\"Linear\" name=\"Z\" componentId=\"z\"><Samples>");
        sb.Append($"<Position dataItemId=\"zpos\" name=\"Z\" sequence=\"1\" timestamp=\"{DateTimeOffset.UtcNow:o}\">{z.ToString("F3", CultureInfo.InvariantCulture)}</Position></Samples></ComponentStream>");
        sb.Append("<ComponentStream component=\"Rotary\" name=\"S\" componentId=\"s\"><Samples>");
        sb.Append($"<RotaryVelocity dataItemId=\"spindle\" name=\"S\" sequence=\"1\" timestamp=\"{DateTimeOffset.UtcNow:o}\">{s.ToString("F0", CultureInfo.InvariantCulture)}</RotaryVelocity></Samples></ComponentStream>");
        sb.Append("<ComponentStream component=\"Path\" name=\"path\" componentId=\"path\"><Samples>");
        sb.Append($"<PathFeedrate dataItemId=\"feed\" name=\"F\" sequence=\"1\" timestamp=\"{DateTimeOffset.UtcNow:o}\">{f.ToString("F2", CultureInfo.InvariantCulture)}</PathFeedrate>");
        sb.Append($"<PartCount dataItemId=\"partcount\" name=\"partcount\" sequence=\"1\" timestamp=\"{DateTimeOffset.UtcNow:o}\">{parts}</PartCount></Samples></ComponentStream>");
        sb.Append("</DeviceStream>");
        sb.Append("</Streams></MTConnectStreams>");
        return sb.ToString();
    }

    private static ushort Reg(ushort[] a, int i) => i >= 0 && i < a.Length ? a[i] : (ushort)0;
}
