using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.JingDiao;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

internal static class JingDiaoLifecycleRegressionTests
{
    public static async Task RunFixtureAsync(string url)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var sessions = new ConcurrentDictionary<string, byte>();
        var failDisconnect = false;
        app.MapGet("/health", () => Results.Json(new
            { status = "ok", service = "jingdiao", protocolVersion = 1, architecture = "x86" }));
        app.MapGet("/fixture/pid", () => Environment.ProcessId);
        app.MapPost("/fixture/fail-disconnect", () => { failDisconnect = true; return Results.Ok(); });
        app.MapPost("/api/jingdiao/connect", () =>
        {
            var sessionId = Guid.NewGuid().ToString("N");
            sessions[sessionId] = 0;
            return Results.Json(new { returnCode = 0, sessionId });
        });
        app.MapPost("/api/jingdiao/ping", (JingDiaoSessionRequest request) =>
            Results.Json(new { returnCode = sessions.ContainsKey(request.SessionId) ? 0 : -404 }));
        app.MapPost("/api/jingdiao/disconnect", (JingDiaoSessionRequest request) =>
        {
            if (failDisconnect) return Results.StatusCode(500);
            sessions.TryRemove(request.SessionId, out _);
            return Results.Json(new { returnCode = 0 });
        });
        await app.RunAsync();
    }

    public static async Task SharedProcessAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var baseUri = new Uri($"http://127.0.0.1:{TestSupport.FreePort()}");
        var config = new DeviceConnectionConfig
        {
            Host = "fixture", Port = 89, ConnectTimeout = TimeSpan.FromSeconds(5),
            ExtendedProperties = new()
            {
                ["ShimBaseUrl"] = baseUri.ToString(),
                ["ShimPath"] = Path.Combine(AppContext.BaseDirectory, "IndustrialIoT.Protocols.Tests.exe"),
            },
        };
        using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(1) };
        await using var first = new JingDiaoDriver(NullLogger<JingDiaoDriver>.Instance);
        await using var second = new JingDiaoDriver(NullLogger<JingDiaoDriver>.Instance);
        var connections = await Task.WhenAll(first.ConnectAsync(config), second.ConnectAsync(config));
        TestSupport.Require(connections.All(result => result.Success),
            "Concurrent shim startup failed: " + string.Join("; ", connections.Select(result => result.ErrorMessage)));
        var originalPid = await http.GetFromJsonAsync<int>("fixture/pid");
        await using var passive = new JingDiaoDriver(NullLogger<JingDiaoDriver>.Instance);
        config.ExtendedProperties["AutoStartShim"] = "false";
        TestSupport.Require((await passive.ConnectAsync(config)).Success, "Passive driver could not join shared shim");
        await first.DisposeAsync();
        TestSupport.Require(await second.PingAsync()
            && await http.GetFromJsonAsync<int>("fixture/pid") == originalPid,
            "Releasing the first driver killed the shared shim");
        using (var crashed = Process.GetProcessById(originalPid))
        {
            crashed.Kill(entireProcessTree: true);
            await crashed.WaitForExitAsync();
        }
        var replacementPid = 0;
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            while (replacementPid == 0)
            {
                await Task.Delay(100, deadline.Token);
                try { replacementPid = await http.GetFromJsonAsync<int>("fixture/pid", deadline.Token); }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            }
        }
        TestSupport.Require(replacementPid != originalPid && await second.PingAsync(),
            "Shim was not independently restarted or its lost session was not restored");
        using var replacement = Process.GetProcessById(replacementPid);
        using var response = await http.PostAsync("fixture/fail-disconnect", null);
        response.EnsureSuccessStatusCode();
        await second.DisconnectAsync();
        TestSupport.Require(second.State == ConnectionState.Disconnected,
            "Disconnect failure left the driver connected");
        TestSupport.Require(!replacement.HasExited && await passive.PingAsync(),
            "AutoStartShim=false user was not counted as a shared process owner");
        await passive.DisposeAsync();
        TestSupport.Require(replacement.WaitForExit(5000), "Last lease failed to stop shim after disconnect HTTP failure");
        await second.DisposeAsync();
        await Task.Delay(2500);
        try
        {
            await http.GetAsync("health");
            throw new InvalidOperationException("Watchdog restarted the shim after the last lease was released");
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
    }

    public static async Task ExternalHealthAsync()
    {
        var baseUri = new Uri($"http://127.0.0.1:{TestSupport.FreePort()}");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(baseUri.ToString());
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var healthMode = 0;
        app.MapGet("/health", () => healthMode switch
        {
            0 => Results.Json(new { status = "ok", service = "jingdiao", protocolVersion = 1, architecture = "x86" }),
            1 => Results.Json(new { error = "DLL missing" }, statusCode: 503),
            _ => Results.Json(new { status = "ok" }),
        });
        await app.StartAsync();
        try
        {
            using (await JingDiaoShimLease.AcquireAsync(baseUri, "missing.exe", TimeSpan.FromSeconds(2), default)) { }
            await JingDiaoShimLease.WaitForReadyAsync(baseUri, TimeSpan.FromSeconds(2), default);
            foreach (var mode in new[] { 1, 2 })
            {
                healthMode = mode;
                var rejected = false;
                try { using var lease = await JingDiaoShimLease.AcquireAsync(baseUri, "missing.exe", TimeSpan.FromSeconds(2), default); }
                catch (InvalidOperationException error)
                { rejected = error.Message.Contains(mode == 1 ? "DLL missing" : "Incompatible"); }
                TestSupport.Require(rejected, "Unhealthy/unrelated HTTP listener was accepted or a duplicate shim was launched");
            }
        }
        finally { await app.StopAsync(); }
    }
}
