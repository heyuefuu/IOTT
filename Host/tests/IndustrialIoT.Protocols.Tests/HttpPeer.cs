using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal sealed class HttpPeer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public int Port { get; }
    public ConcurrentDictionary<string, byte[]> Files { get; } = new();
    public ConcurrentDictionary<string, JsonNode> Values { get; } = new();
    public List<string> Requests { get; } = [];
    public ConcurrentQueue<(string DeviceId, JsonElement Body)> DataRequests { get; } = new();
    public bool RejectWrites { get; set; }

    public HttpPeer()
    {
        Port = TestSupport.FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.MapPost("/v1/{deviceId}/data/", async (string deviceId, HttpRequest request) =>
        {
            if (deviceId == "silent") await Task.Delay(5000, request.HttpContext.RequestAborted);
            if (deviceId == "offline") return Results.Json(new { status = "FAILED", code = 1001, value = new object[0] });
            using var body = await JsonDocument.ParseAsync(request.Body);
            DataRequests.Enqueue((deviceId, body.RootElement.Clone()));
            var operation = body.RootElement.GetProperty("operation").GetString();
            var rows = new List<object>();
            foreach (var item in body.RootElement.GetProperty("items").EnumerateArray())
            {
                var path = item.GetProperty("path").GetString()!;
                Requests.Add(operation + ":" + path);
                switch (operation)
                {
                    case "set_value":
                        if (!RejectWrites)
                            Values[path] = JsonNode.Parse(item.GetProperty("value").GetRawText())!;
                        rows.Add(new[] { !RejectWrites });
                        break;
                    case "get_keys":
                        rows.Add(Files.Keys.ToArray());
                        break;
                    case "get_attributes":
                        rows.Add(item.GetProperty("key").EnumerateArray().Select(key =>
                            new { type = "file", size = Files[key.GetString()!].Length, changeTime = "2026/09/22 12:00:00" }).ToArray());
                        break;
                    default:
                        rows.Add(new[] { Values.GetValueOrDefault(path) ?? JsonValue.Create(path.EndsWith("STATUS") ? "READY" : "0")! });
                        break;
                }
            }
            return Results.Json(new { status = "SUCCESS", code = 0, value = rows });
        });
        _app.MapPost("/v1/{deviceId}/file/", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            using var bytes = new MemoryStream();
            await form.Files["value"]!.CopyToAsync(bytes);
            Files[form["key"].ToString()] = bytes.ToArray();
            return Results.Json(new { status = "SUCCESS", code = 0, value = new[] { new[] { true } } });
        });
        _app.MapGet("/v1/{deviceId}/file/", (string key) => Results.Bytes(Files[key]));
        _app.MapGet("/probe", () => Results.Text("""
            <MTConnectDevices xmlns="urn:mtconnect.org:MTConnectDevices:2.0"><Devices>
              <Device id="machine" name="fixture"><DataItems>
                <DataItem id="count" name="count" type="PART_COUNT" category="SAMPLE" />
              </DataItems><Components><Controller id="controller" name="controller"><DataItems>
                <DataItem id="state" name="state" type="EXECUTION" category="EVENT" />
              </DataItems><Components><Path id="path" name="path"><DataItems>
                <DataItem id="feed" name="feed" type="PATH_FEEDRATE" category="SAMPLE" />
              </DataItems></Path></Components></Controller></Components></Device>
            </Devices></MTConnectDevices>
            """, "application/xml"));
        _app.MapGet("/current", () => Results.Text("""
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.0"><Streams>
              <DeviceStream><ComponentStream><Samples>
                <PartCount dataItemId="count" timestamp="2026-09-22T04:00:00Z">42</PartCount>
                <PathFeedrate dataItemId="feed" timestamp="2026-09-22T04:00:00Z">12.5</PathFeedrate>
              </Samples><Events><Execution dataItemId="state">READY</Execution></Events>
              </ComponentStream></DeviceStream></Streams></MTConnectStreams>
            """, "application/xml"));
    }

    public Task StartAsync() => _app.StartAsync();
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
