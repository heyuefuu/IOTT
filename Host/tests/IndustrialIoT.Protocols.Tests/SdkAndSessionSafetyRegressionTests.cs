using System.Globalization;
using System.Reflection;
using System.Text.Json;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Gsk;
using IndustrialIoT.Protocols.HncSdk;
using IndustrialIoT.Protocols.JingDiao;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.NCLink;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ISession = Opc.Ua.Client.ISession;

internal static class SdkAndSessionSafetyRegressionTests
{
    public static async Task RunAsync()
    {
        await VerifySdkValuesAsync();
        await VerifyDisconnectedSessionAsync();
        await using var gsk = new GskrmDriver(NullLogger<GskrmDriver>.Instance);
        TestSupport.Require(!(await gsk.BrowseAsync("/status")).Any(node => node.Path == "Status.Estop"),
            "Unsupported GSK emergency-stop tag must not be advertised");
    }

    private static async Task VerifySdkValuesAsync()
    {
        var port = TestSupport.FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/health", () => Results.Ok());
        foreach (var prefix in new[] { "/api/hnc-sdk", "/api/jingdiao" })
        {
            app.MapPost(prefix + "/connect", () => Results.Json(new { returnCode = 0, sessionId = "fixture" }));
            app.MapPost(prefix + "/disconnect", () => Results.Json(new { returnCode = 0 }));
        }
        app.MapPost("/api/hnc-sdk/read", (JsonElement request) =>
        {
            object? value = request.GetProperty("address").GetString() switch
            {
                "Null" => null, "Overflow" => 40000, "Invalid" => "invalid", "Valid" => "1.25",
                "Unsigned" => ulong.MaxValue, _ => 1,
            };
            return Results.Json(new { returnCode = 0, value });
        });
        app.MapPost("/api/jingdiao/get-part-count", () => Results.Json(new { returnCode = 0, value = 40000 }));
        await app.StartAsync();
        try
        {
            var config = new DeviceConnectionConfig
            {
                Host = "127.0.0.1", Port = port,
                ExtendedProperties = new() { ["ShimBaseUrl"] = $"http://127.0.0.1:{port}", ["AutoStartShim"] = "false" },
            };
            await using var hnc = new HncSdkDriver(NullLogger<HncSdkDriver>.Instance);
            TestSupport.Require((await hnc.ConnectAsync(config)).Success, "HNC fixture connection failed");
            var before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var values = await hnc.ReadTagsAsync([
                    new() { Address = "Null", DataType = DataType.Double },
                    new() { Address = "Overflow", DataType = DataType.Int16 },
                    new() { Address = "Invalid", DataType = DataType.Double },
                    new() { Address = "Valid", DataType = DataType.Double },
                    new() { Address = "Unsigned", DataType = DataType.UInt64 },
                ]);
                TestSupport.Require(values.Take(3).All(value => value.Quality == TagQuality.Bad
                    && !string.IsNullOrEmpty(value.ErrorMessage)), "HNC invalid values must be diagnosed as Bad");
                TestSupport.Require(values[3].Quality == TagQuality.Good && Equals(values[3].Value, 1.25d),
                    "A bad point or local culture corrupted a later valid HNC point");
                TestSupport.Require(values[4].Quality == TagQuality.Good
                    && Equals(values[4].Value, "18446744073709551615"), "HNC UInt64 lost JSON precision");
            }
            finally { CultureInfo.CurrentCulture = before; }
            await using var jd = new JingDiaoDriver(NullLogger<JingDiaoDriver>.Instance);
            TestSupport.Require((await jd.ConnectAsync(config)).Success, "JingDiao fixture connection failed");
            var invalid = await jd.ReadTagAsync("State:PartCount", DataType.Int16);
            var valid = await jd.ReadTagAsync("State:PartCount", DataType.Int32);
            TestSupport.Require(invalid.Quality == TagQuality.Bad && valid.Quality == TagQuality.Good
                && Equals(valid.Value, 40000), "JingDiao overflow must fail without masking valid reads");
        }
        finally { await app.StopAsync(); }
    }

    private static async Task VerifyDisconnectedSessionAsync()
    {
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        var session = DispatchProxy.Create<ISession, DisconnectedSession>();
        var proxy = (DisconnectedSession)(object)session;
        await using var driver = new OpcUaDriver(NullLogger<OpcUaDriver>.Instance);
        var field = typeof(OpcUaDriver).GetField("_session", hidden)!;
        field.SetValue(driver, session);
        await driver.DisconnectAsync();
        TestSupport.Require(proxy.Disposals == 1 && proxy.Unsubscribed && field.GetValue(driver) is null,
            "An already disconnected OPC UA session must still unsubscribe and dispose");
        await driver.DisconnectAsync();
        TestSupport.Require(proxy.Disposals == 1, "Repeated disconnect disposed the old session twice");
        var endpoint = (Uri)typeof(OpcUaDriver).GetMethod("ResolveEndpointUri", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new DeviceConnectionConfig
            {
                Host = "wrong-host", Port = 1111,
                ExtendedProperties = new() { ["EndpointUrl"] = "opc.tcp://right-host:4845/path" },
            }])!;
        TestSupport.Require(endpoint.DnsSafeHost == "right-host" && endpoint.Port == 4845,
            "OPC UA endpoint override must control both probe host and port");
    }

    public class DisconnectedSession : DispatchProxy
    {
        public int Disposals { get; private set; }
        public bool Unsubscribed { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "get_Connected") return false;
            if (targetMethod.Name == "Dispose") { Disposals++; return null; }
            if (targetMethod.Name == "remove_KeepAlive") { Unsubscribed = true; return null; }
            throw new InvalidOperationException($"Unexpected session call: {targetMethod.Name}");
        }
    }
}
