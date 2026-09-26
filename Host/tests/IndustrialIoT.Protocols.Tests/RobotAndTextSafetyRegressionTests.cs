using System.Net;
using System.Net.Sockets;
using System.Text;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Haas;
using IndustrialIoT.Protocols.MTConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

internal static class RobotAndTextSafetyRegressionTests
{
    public static async Task RunAsync()
    {
        await HaasRejectsErrorResponseAsync();
        await MTConnectRejectsInvalidNumberAsync();
    }

    private static async Task HaasRejectsErrorResponseAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            TestSupport.Require(await reader.ReadLineAsync(timeout.Token) == "Q104", "Wrong MDC request");
            await stream.WriteAsync(Encoding.ASCII.GetBytes("?\r\n"), timeout.Token);
        }, timeout.Token);
        await using var driver = new HaasMdcDriver(NullLogger<HaasMdcDriver>.Instance);
        var config = new DeviceConnectionConfig
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            ConnectTimeout = TimeSpan.FromSeconds(2), ReadTimeout = TimeSpan.FromSeconds(2),
        };
        TestSupport.Require((await driver.ConnectAsync(config, timeout.Token)).Success, "MDC connect failed");
        var tag = await driver.ReadTagAsync("Mode", DataType.String, timeout.Token);
        TestSupport.Require(tag.Quality == TagQuality.Bad, "MDC error response was treated as data");
        await peer;
    }

    private static async Task MTConnectRejectsInvalidNumberAsync()
    {
        var port = TestSupport.FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/probe", () => Results.Text("<MTConnectDevices/>", "application/xml"));
        app.MapGet("/current", () => Results.Text("""
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.0"><Streams>
              <DeviceStream><ComponentStream><Samples>
                <Position dataItemId="position">invalid-number</Position>
              </Samples></ComponentStream></DeviceStream>
            </Streams></MTConnectStreams>
            """, "application/xml"));
        await app.StartAsync();
        await using var driver = new MTConnectDriver(NullLogger<MTConnectDriver>.Instance);
        var config = new DeviceConnectionConfig
        {
            Host = "127.0.0.1", Port = port,
            ConnectTimeout = TimeSpan.FromSeconds(2), ReadTimeout = TimeSpan.FromSeconds(2),
        };
        TestSupport.Require((await driver.ConnectAsync(config)).Success, "MTConnect connect failed");
        var tag = await driver.ReadTagAsync("position", DataType.Float);
        TestSupport.Require(tag.Quality == TagQuality.Bad, "Invalid numeric sample was marked Good");
        await app.StopAsync();
    }
}
