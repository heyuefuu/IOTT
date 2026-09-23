namespace MachineConnectionApi.Tests;

using System.Collections.Concurrent;
using System.Net;
using MachineConnectionApi.Options;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

internal static partial class InfluxSettingsRegressionTests
{
    private static async Task WriterSwitchesConnectionsWithoutInterruptingInflightWrites()
    {
        var firstReached = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterHeaders = new ConcurrentQueue<string>();
        await using var firstServer = await StartWriteServer(async context =>
        {
            firstReached.TrySetResult(context.Request.Headers.Authorization.ToString());
            await releaseFirst.Task.WaitAsync(context.RequestAborted);
        });
        await using var nextServer = await StartWriteServer(context =>
        {
            laterHeaders.Enqueue(context.Request.Headers.Authorization.ToString());
            return Task.CompletedTask;
        });
        var fileName = $"influx-switch-{Guid.NewGuid():N}.json";
        using var provider = CreateProvider(fileName, firstServer.Urls.Single());
        var monitor = provider.GetRequiredService<IOptionsMonitor<InfluxDbOptions>>();
        var store = provider.GetRequiredService<InfluxSettingsStore>();
        using var writer = new InfluxTelemetryWriter(monitor, NullLogger<InfluxTelemetryWriter>.Instance);
        var points = new[] { new InfluxTelemetryPoint("fixture", "ns=2;i=1", "Double", 1.5, "Good", "", "成功", null) };
        try
        {
            var pending = writer.WriteBatchAsync("fixture-device", DateTimeOffset.UtcNow, points);
            var firstHeader = await firstReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            store.Save(Settings(nextServer.Urls.Single()) with { Token = "fixture-rotated" }, monitor.CurrentValue);
            await writer.WriteBatchAsync("fixture-device", DateTimeOffset.UtcNow, points).WaitAsync(TimeSpan.FromSeconds(5));
            store.Save(Settings(nextServer.Urls.Single()) with { Token = "fixture-rotated-again" }, monitor.CurrentValue);
            await writer.WriteBatchAsync("fixture-device", DateTimeOffset.UtcNow, points).WaitAsync(TimeSpan.FromSeconds(5));
            releaseFirst.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            var headers = laterHeaders.ToArray();
            Check(firstHeader.EndsWith("fixture-token", StringComparison.Ordinal), "Initial writer did not use the configured credential");
            Check(headers.Length == 2 && headers[0].EndsWith("fixture-rotated", StringComparison.Ordinal) &&
                headers[1].EndsWith("fixture-rotated-again", StringComparison.Ordinal), "Writer did not switch address and credentials");
        }
        finally
        {
            releaseFirst.TrySetResult();
            File.Delete(SettingsPath(fileName));
        }
    }

    private static async Task<WebApplication> StartWriteServer(Func<HttpContext, Task> capture)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapPost("/api/v2/write", async context =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            await capture(context);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        });
        await app.StartAsync();
        return app;
    }
}
