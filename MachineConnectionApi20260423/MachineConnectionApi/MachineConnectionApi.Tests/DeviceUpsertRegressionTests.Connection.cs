namespace MachineConnectionApi.Tests;

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MachineConnectionApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class DeviceUpsertRegressionTests
{
    private static async Task ConnectionTestsDistinguishDriverAndTcp()
    {
        foreach (var success in new[] { true, false })
        {
            var device = CredentialDevice() with { Status = success ? "Offline" : "Online" };
            var store = new MemoryDeviceStore(device);
            using var handler = new ConnectionResponseHandler(HttpStatusCode.OK,
                JsonSerializer.Serialize(new { success, errorMessage = success ? null : "Device rejected handshake" }));
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://host.test/") };
            var controller = ConnectionController(store, client);
            var result = ConnectionJson(await controller.TestConnection(device.Id, CancellationToken.None));
            Expect(result.GetProperty("mode").GetString() == "driver", "Driver results must not fall back to TCP");
            Expect(result.GetProperty("success").GetBoolean() == success, "Driver result must be preserved");
            Expect(store.ReadAll().Single().Status == (success ? "Online" : "Error"), "Only driver results set device status");
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var offline = CredentialDevice() with { Host = "127.0.0.1", Port = port, ConnectTimeoutMs = 500 };
        var tcpStore = new MemoryDeviceStore(offline);
        using var unavailable = new ConnectionResponseHandler(HttpStatusCode.ServiceUnavailable, "Host unavailable");
        using var tcpClient = new HttpClient(unavailable) { BaseAddress = new Uri("http://host.test/") };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tcpResult = ConnectionJson(await ConnectionController(tcpStore, tcpClient).TestConnection(offline.Id, timeout.Token));
        using var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
        Expect(tcpResult.GetProperty("mode").GetString() == "tcp" && tcpResult.GetProperty("success").GetBoolean(),
            "TCP reachability must be explicitly identified");
        Expect(!string.IsNullOrWhiteSpace(tcpResult.GetProperty("errorMessage").GetString()), "TCP result must explain missing driver verification");
        Expect(tcpStore.ReadAll().Single().Status == "Offline" && tcpStore.ReadAll().Single().LastSeenAt == offline.LastSeenAt,
            "TCP-only reachability must not mark a CNC online or update its last device contact");
        foreach (var protocol in new[] { "Gskrm", "GskrmFileTransfer" })
        {
            var sdkStore = new MemoryDeviceStore(offline with { Protocol = protocol });
            var sdkResult = ConnectionJson(await ConnectionController(sdkStore, tcpClient).TestConnection(offline.Id, timeout.Token));
            Expect(!sdkResult.GetProperty("success").GetBoolean() && sdkResult.GetProperty("mode").GetString() == "driver",
                "SDK-managed protocols require driver verification instead of an arbitrary TCP port probe");
            Expect(!listener.Pending() && sdkStore.ReadAll().Single().Status == "Offline", "SDK fallback must not open TCP or mark a device online");
        }
        Console.WriteLine("PASS connection tests preserve driver failures and distinguish TCP reachability");
    }

    private static DevicesController ConnectionController(MemoryDeviceStore store, HttpClient client) => new(
        new CredentialClientFactory(client), new ConfigurationBuilder().Build(), store,
        new StubSyncService(), new StubActivityLog(), NullLogger<DevicesController>.Instance);

    private static JsonElement ConnectionJson(IActionResult result) => result is OkObjectResult response
        ? JsonSerializer.SerializeToElement(response.Value)
        : throw new InvalidOperationException("Connection test must return an explicit result");

    private sealed class ConnectionResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
