namespace MachineConnectionApi.Tests;

using System.Net;
using System.Text.Json;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class DeviceUpsertRegressionTests
{
    private static async Task EmptyRegistryRestorePreservesDevicesWithoutWritingUpstream()
    {
        var source = CredentialDevice() with { ExtendedProperties = new() { ["DeviceCode"] = "h001" } };
        var store = new MemoryDeviceStore();
        using var handler = new RestoreHandler(JsonSerializer.Serialize(new[] { source }));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var service = RestoreService(client, store);
        var report = await service.SyncAllAsync(CancellationToken.None);
        var saved = store.ReadAll().Single();
        Expect(report.Restored == 1 && report.Failed == 0, "An empty registry must restore upstream devices");
        Expect(saved.Id == source.Id && saved.CreatedAt == source.CreatedAt, "Restore must preserve identity and creation time");
        Expect(saved.ExtendedProperties["DeviceCode"] == "h001" && saved.Transfer?.Host == source.Transfer?.Host,
            "Restore must retain extension fields and the independent transfer channel");
        Expect(saved.Password is null && saved.Transfer?.Password is null, "Restore must not import credentials from list responses");
        Expect(saved.RestoredFromUpstream && saved.UpstreamSynced == true, "Restored devices must be marked to avoid destructive writeback");
        var persisted = JsonSerializer.Deserialize<MachineDeviceDto>(JsonSerializer.Serialize(saved))!;
        var reloadedStore = new MemoryDeviceStore(persisted);
        report = await RestoreService(client, reloadedStore).SyncAllAsync(CancellationToken.None);
        Expect(report.Skipped == 1 && report.Created == 0 && report.Updated == 0, "Later sync must skip restored device writeback");
        Expect(handler.Methods.All(method => method == HttpMethod.Get), "Restoration and later sync must only read upstream");
        Expect(handler.Paths[0] == "/api/Devices", "Restoration must fetch the complete device list");
        var edited = GetOkValue(await CreateController(reloadedStore).Update(saved.Id,
            new MachineDeviceUpsertRequest { Name = "Explicit edit" }, CancellationToken.None));
        Expect(!edited.RestoredFromUpstream, "An explicit edit must allow normal synchronization and retries");
    }

    private static async Task EmptyRegistryRestoreRejectsInvalidResponses()
    {
        var valid = CredentialDevice();
        var bodies = new[]
        {
            "not-json", "null", "[null]", "[{}]",
            JsonSerializer.Serialize(new[] { valid, valid }),
            JsonSerializer.Serialize(new[] { valid with { Id = "" } }),
            JsonSerializer.Serialize(new[] { valid with { Name = "" } }),
            JsonSerializer.Serialize(new[] { valid with { Host = "" } }),
        };
        foreach (var body in bodies)
        {
            var store = new MemoryDeviceStore();
            using var handler = new RestoreHandler(body);
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            UpstreamSyncReport? report = null;
            try { report = await RestoreService(client, store).SyncAllAsync(CancellationToken.None); }
            catch (Exception) { }
            Expect(store.ReadAll().Count == 0, "An invalid upstream list must never partially populate the registry");
            Expect(report is null || report.Failed > 0, "Invalid upstream data must not be reported as a successful empty restore");
        }
        foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable })
        {
            var store = new MemoryDeviceStore();
            using var handler = new RestoreHandler("[]", status);
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            UpstreamSyncReport? report = null;
            try { report = await RestoreService(client, store).SyncAllAsync(CancellationToken.None); }
            catch (Exception) when (status != HttpStatusCode.OK) { }
            Expect(store.ReadAll().Count == 0, "An empty or unavailable upstream must leave the registry empty");
            Expect(status == HttpStatusCode.OK ? report is { Failed: 0, Restored: 0 } : report is null || report.Failed > 0,
                "A failed HTTP response must be distinguishable from a valid empty list");
        }
    }

    private static async Task EmptyRegistryRestoreKeepsConcurrentLocalChanges()
    {
        var remote = CredentialDevice();
        var local = remote with { Name = "Concurrent local edit", Password = "local-secret" };
        var store = new MemoryDeviceStore();
        using var handler = new RestoreHandler(JsonSerializer.Serialize(new[] { remote }), onRead: () => store.WriteAll([local]));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await RestoreService(client, store).SyncAllAsync(CancellationToken.None);
        var saved = store.ReadAll().Single();
        Expect(saved.Name == local.Name && saved.Password == local.Password,
            "A local change made while restore is fetching must not be overwritten");
        Expect(handler.Methods.All(method => method == HttpMethod.Get), "A restore race must never trigger upstream writes");
    }

    private static DeviceUpstreamSyncService RestoreService(HttpClient client, IDeviceStore store) => new(
        new CredentialClientFactory(client), store, new ConfigurationBuilder().Build(),
        NullLogger<DeviceUpstreamSyncService>.Instance);

    private sealed class RestoreHandler(string body, HttpStatusCode status = HttpStatusCode.OK, Action? onRead = null) : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Methods.Add(request.Method);
            Paths.Add(request.RequestUri!.AbsolutePath);
            onRead?.Invoke();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
