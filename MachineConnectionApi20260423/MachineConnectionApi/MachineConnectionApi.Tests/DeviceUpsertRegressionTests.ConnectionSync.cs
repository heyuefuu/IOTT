namespace MachineConnectionApi.Tests;

using System.Net;
using System.Text.Json;
using MachineConnectionApi.Controllers;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class DeviceUpsertRegressionTests
{
    private static async Task ConnectionRetriesPendingConfiguration()
    {
        foreach (var recover in new[] { true, false })
        {
            var device = CredentialDevice() with { Protocol = "GskWebServer", UpstreamSynced = true };
            var store = new MemoryDeviceStore(device);
            using var handler = new ConnectionSyncHandler { RejectSync = true };
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://host.test/") };
            var controller = SyncConnectionController(store, client);
            var saved = GetOkValue(await controller.Update(device.Id, new MachineDeviceUpsertRequest
            {
                Host = "192.0.2.17", Port = 11521, Username = "gsk-user", Password = "fixture-secret",
                ExtendedProperties = new() { ["DeviceSn"] = "gsk-new", ["Scheme"] = "http" },
            }, CancellationToken.None));
            Expect(saved.UpstreamSynced == false, "Failed update must remain pending in the gateway");
            handler.RejectSync = !recover;
            var result = ConnectionJson(await controller.TestConnection(device.Id, CancellationToken.None));
            Expect(handler.SyncAttempts == 2, "Connection must retry pending configuration even when the Host device exists");
            Expect(handler.ConnectionTests == (recover ? 1 : 0), "A failed sync must never test stale Host configuration");
            Expect(result.GetProperty("success").GetBoolean() == recover, "Connection must reflect the synchronization outcome");
            var stored = store.ReadAll().Single();
            Expect(stored.UpstreamSynced == recover, "Connection retry must persist synchronization status");
            if (!recover)
            {
                Expect(result.GetProperty("errorMessage").GetString()!.Contains("503"), "Expose the sync failure");
                continue;
            }
            Expect(stored.UpstreamError is null, "Successful retry must clear the previous error");
            var used = handler.ConfigUsedForTest!.Value;
            Expect(used.GetProperty("host").GetString() == "192.0.2.17"
                && used.GetProperty("port").GetInt32() == 11521, "Test must use the newly saved gateway endpoint");
            Expect(used.GetProperty("extendedProperties").GetProperty("DeviceSn").GetString() == "gsk-new"
                && used.GetProperty("password").GetString() == "fixture-secret", "Test must use new SN and credentials");
        }
        Console.WriteLine("PASS connection retries pending GSK configuration and blocks stale tests after sync failure");
    }

    private static async Task ConnectionPreservesRestoredConfiguration()
    {
        var device = CredentialDevice() with { RestoredFromUpstream = true, UpstreamSynced = false, Password = null };
        var store = new MemoryDeviceStore(device);
        using var handler = new ConnectionSyncHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://host.test/") };
        var result = ConnectionJson(await SyncConnectionController(store, client).TestConnection(device.Id, CancellationToken.None));
        Expect(handler.SyncAttempts == 0 && handler.ConnectionTests == 1, "Restored snapshots must not overwrite Host credentials");
        Expect(result.GetProperty("success").GetBoolean(), "Restored devices can still use their Host configuration");
    }

    private static DevicesController SyncConnectionController(MemoryDeviceStore store, HttpClient client)
    {
        var factory = new CredentialClientFactory(client);
        var configuration = new ConfigurationBuilder().Build();
        var sync = new DeviceUpstreamSyncService(factory, store, configuration, NullLogger<DeviceUpstreamSyncService>.Instance);
        return new DevicesController(factory, configuration, store, sync, new StubActivityLog(), NullLogger<DevicesController>.Instance);
    }

    private sealed class ConnectionSyncHandler : HttpMessageHandler
    {
        public bool RejectSync { get; set; }
        public int SyncAttempts { get; private set; }
        public int ConnectionTests { get; private set; }
        public JsonElement? ConfigUsedForTest { get; private set; }
        private JsonElement? _configuration;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) return Response(HttpStatusCode.OK, "{}");
            if (request.Method == HttpMethod.Put)
            {
                SyncAttempts++;
                if (RejectSync) return Response(HttpStatusCode.ServiceUnavailable, "fixture sync unavailable");
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                _configuration = json.RootElement.Clone();
                return Response(HttpStatusCode.OK, "{}");
            }
            Expect(request.RequestUri!.AbsolutePath.EndsWith("/test-connection"), "Unexpected upstream request");
            ConnectionTests++;
            ConfigUsedForTest = _configuration;
            return Response(HttpStatusCode.OK, "{\"success\":true}");
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body) };
    }
}
