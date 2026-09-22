using System.Net.WebSockets;
using System.Text;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Gsk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

internal static class GskRealtimeRegressionTests
{
    public static async Task RunAsync()
    {
        var port = TestSupport.FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/api/v1/cnc/mc", () => Results.Json(new { model = "fixture" }));
        var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Map("/ws/cnc", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"state\":2,\"mode\":1}"),
                WebSocketMessageType.Text, true, context.RequestAborted);
            await close.Task.WaitAsync(context.RequestAborted);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fixture ended", context.RequestAborted);
        });
        await app.StartAsync();
        try
        {
            await using var driver = new GskWebServerDriver(NullLogger<GskWebServerDriver>.Instance);
            var connection = await driver.ConnectAsync(new DeviceConnectionConfig
            {
                Host = "127.0.0.1", Port = port, ReadTimeout = TimeSpan.FromSeconds(2),
            });
            TestSupport.Require(connection.Success, connection.ErrorMessage ?? "GSK HTTP connect failed");
            TestSupport.Require(await driver.PingAsync(), "GSK HTTP ping failed");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var first = await driver.ReadTagAsync("Realtime.State", DataType.Int32, timeout.Token);
            TestSupport.Require(first.Quality == TagQuality.Good && Equals(first.Value, 2), "Realtime frame not received");
            await Task.Delay(50, timeout.Token);
            var sameFrame = await driver.ReadTagAsync("Realtime.State", DataType.Int32, timeout.Token);
            TestSupport.Require(sameFrame.Timestamp == first.Timestamp, "Cached frame was assigned a new sample time");
            close.TrySetResult();
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var value = await driver.ReadTagAsync("Realtime.State", DataType.Int32, timeout.Token);
                if (value.Quality == TagQuality.Bad) break;
                await Task.Delay(20, timeout.Token);
            }
            TestSupport.Require(await driver.PingAsync(), "HTTP peer should remain healthy after WebSocket closes");
        }
        finally
        {
            close.TrySetResult();
            await app.StopAsync();
        }
    }
}
