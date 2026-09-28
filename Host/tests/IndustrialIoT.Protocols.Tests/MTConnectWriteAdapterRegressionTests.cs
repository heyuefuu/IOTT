using System.Text.Json;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.MTConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

internal static class MTConnectWriteAdapterRegressionTests
{
    public static async Task RunAsync()
    {
        var port = TestSupport.FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var writes = new List<JsonElement>();
        var writeTokens = new List<string>();
        var probeTokens = new List<string>();
        var rejectWrites = false;
        app.MapGet("/probe", (HttpRequest request) =>
        {
            probeTokens.Add(request.Headers.Authorization.ToString());
            return Results.Text("""
                <MTConnectDevices><Devices><Device id="machine" name="fixture"><DataItems>
                  <DataItem id="enabled" type="EXECUTION" category="EVENT"/>
                  <DataItem id="count" type="PART_COUNT" category="SAMPLE"/>
                  <DataItem id="state" type="EXECUTION" category="EVENT"/>
                </DataItems><Components><Controller id="controller"><DataItems>
                  <DataItem id="feed" type="PATH_FEEDRATE" category="SAMPLE"/>
                </DataItems></Controller></Components></Device></Devices></MTConnectDevices>
                """, "application/xml");
        });
        app.MapPost("/vendor-write", async (HttpRequest request) =>
        {
            using var document = await JsonDocument.ParseAsync(request.Body);
            writes.Add(document.RootElement.Clone());
            writeTokens.Add(request.Headers.Authorization.ToString());
            return rejectWrites ? Results.StatusCode(409) : Results.Ok();
        });
        await app.StartAsync();
        var config = new DeviceConnectionConfig { Host = "127.0.0.1", Port = port };
        await using (var readOnly = new MTConnectDriver(NullLogger<MTConnectDriver>.Instance))
        {
            TestSupport.Require((await readOnly.ConnectAsync(config)).Success, "Read-only probe failed");
            TestSupport.Require(!readOnly.Capabilities.HasFlag(DriverCapabilities.Write), "Default driver advertises writes");
            TestSupport.Require(Flatten(await readOnly.BrowseAsync()).All(node => !node.IsWritable), "Default browse is writable");
            TestSupport.Require(!(await readOnly.WriteTagAsync("count", DataType.Int32, 1)).Success, "Default write succeeded");
        }
        var endpoint = $"http://127.0.0.1:{port}/vendor-write";
        foreach (var properties in new Dictionary<string, string>[]
        {
            new() { ["WriteEndpointUrl"] = endpoint },
            new() { ["WriteEndpointUrl"] = "file:///write", ["WriteAddresses"] = "count" },
            new() { ["WriteAddresses"] = "count" },
        })
        {
            await using var invalid = new MTConnectDriver(NullLogger<MTConnectDriver>.Instance);
            var connected = await invalid.ConnectAsync(config with { ExtendedProperties = properties });
            TestSupport.Require(!connected.Success && connected.ErrorMessage?.Contains("Write") == true, "Invalid write config accepted");
        }
        await using var driver = new MTConnectDriver(NullLogger<MTConnectDriver>.Instance);
        TestSupport.Require((await driver.ConnectAsync(config with
        {
            ExtendedProperties = new()
            {
                ["WriteEndpointUrl"] = endpoint, ["WriteAddresses"] = " enabled, count;feed\r\ncount ",
                ["WriteBearerToken"] = "fixture-token",
            },
        })).Success, "Vendor adapter configuration failed");
        TestSupport.Require(driver.Capabilities.HasFlag(DriverCapabilities.Write), "Configured adapter does not advertise Write");
        var nodes = Flatten(await driver.BrowseAsync()).ToDictionary(node => node.Path);
        TestSupport.Require(nodes["enabled"].IsWritable && nodes["count"].IsWritable && nodes["feed"].IsWritable,
            "Allowed variable missing write access");
        TestSupport.Require(!nodes["state"].IsWritable && !nodes["machine"].IsWritable, "Unlisted variable/folder became writable");
        using (var exported = await driver.ExportAddressSpaceAsync(ExportFormat.JSON))
        using (var document = await JsonDocument.ParseAsync(exported))
        {
            var writable = document.RootElement.EnumerateArray().Where(node => node.GetProperty("IsWritable").GetBoolean())
                .Select(node => node.GetProperty("Path").GetString()).ToHashSet();
            TestSupport.Require(writable.SetEquals(["enabled", "count", "feed"]), "JSON export write access differs from browsing");
        }
        foreach (var sample in new (string Address, DataType Type, object Value)[]
        {
            ("enabled", DataType.Bool, true), ("count", DataType.Int32, 553), ("feed", DataType.Double, 12.5),
        })
        {
            TestSupport.Require((await driver.WriteTagAsync(sample.Address, sample.Type, sample.Value)).Success, "Vendor write failed");
            var request = writes.Last();
            TestSupport.Require(request.GetProperty("address").GetString() == sample.Address &&
                request.GetProperty("dataType").GetString() == sample.Type.ToString() &&
                request.GetProperty("value").GetRawText() == JsonSerializer.Serialize(sample.Value), "Vendor payload lost type/value");
        }
        foreach (var address in new[] { "state", "COUNT", "*" })
            TestSupport.Require(!(await driver.WriteTagAsync(address, DataType.Int32, 1)).Success, "Unlisted address wrote");
        TestSupport.Require(writes.Count == 3, "Blocked writes reached the adapter");
        await driver.BrowseAsync();
        TestSupport.Require(writeTokens.All(token => token == "Bearer fixture-token") && probeTokens.All(string.IsNullOrEmpty),
            "Vendor credentials missing or leaked to MTConnect probe");
        rejectWrites = true;
        TestSupport.Require(!(await driver.WriteTagAsync("count", DataType.Int32, 1)).Success, "Adapter rejection ignored");
        await driver.DisconnectAsync();
        TestSupport.Require(!(await driver.WriteTagAsync("count", DataType.Int32, 1)).Success, "Disconnected driver wrote");
        TestSupport.Require(writes.Count == 4, "Disconnected write reached adapter");
        await app.StopAsync();
    }

    private static IEnumerable<AddressNode> Flatten(IEnumerable<AddressNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node.Children is not null)
                foreach (var child in Flatten(node.Children)) yield return child;
        }
    }
}
