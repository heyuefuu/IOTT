using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.HncSdk;
using IndustrialIoT.Protocols.JingDiao;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

internal static class SdkDirectoryRegressionTests
{
    public static async Task RunAsync()
    {
        var port = TestSupport.FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var failDirectory = false;
        app.MapGet("/health", () => Results.Ok());
        foreach (var prefix in new[] { "/api/hnc-sdk", "/api/jingdiao" })
        {
            app.MapPost(prefix + "/connect", () => Results.Json(new { returnCode = 0, sessionId = "fixture" }));
            app.MapPost(prefix + "/disconnect", () => Results.Json(new { returnCode = 0 }));
            app.MapPost(prefix + "/ping", () => Results.Json(new { returnCode = 0 }));
        }
        app.MapPost("/api/hnc-sdk/read", () => Results.Json(new { returnCode = 0, value = 1 }));
        IResult Files() => failDirectory
            ? Results.Json(new { returnCode = 22, errorMessage = "fixture directory offline" })
            : Results.Json(new { returnCode = 0, value = new[]
                { new { path = "/fixture.nc", name = "fixture.nc", isDirectory = false, sizeBytes = 12 } } });
        app.MapPost("/api/hnc-sdk/files", Files);
        app.MapPost("/api/jingdiao/list-files", Files);
        await app.StartAsync();
        try
        {
            foreach (var hnc in new[] { true, false })
            {
                await using IProtocolDriver driver = hnc
                    ? new HncSdkDriver(NullLogger<HncSdkDriver>.Instance)
                    : new JingDiaoDriver(NullLogger<JingDiaoDriver>.Instance);
                var connection = await driver.ConnectAsync(new DeviceConnectionConfig
                {
                    Host = "127.0.0.1", Port = port, ConnectTimeout = TimeSpan.FromSeconds(3),
                    ExtendedProperties = new()
                    {
                        ["ShimBaseUrl"] = $"http://127.0.0.1:{port}", ["AutoStartShim"] = "false",
                    },
                });
                TestSupport.Require(connection.Success, connection.ErrorMessage ?? "IPC connect failed");
                TestSupport.Require(await driver.PingAsync(), "IPC ping failed");
                var browser = (IProgramFileBrowser)driver;
                failDirectory = false;
                var files = await browser.BrowseFilesAsync();
                TestSupport.Require(files.Single().Path == "/fixture.nc", "IPC directory response was lost");
                failDirectory = true;
                var threw = false;
                try { await browser.BrowseFilesAsync(); }
                catch (IOException error) { threw = error.Message.Contains("22"); }
                TestSupport.Require(threw, "SDK directory error was converted into an empty listing");
            }
        }
        finally { await app.StopAsync(); }
    }
}
