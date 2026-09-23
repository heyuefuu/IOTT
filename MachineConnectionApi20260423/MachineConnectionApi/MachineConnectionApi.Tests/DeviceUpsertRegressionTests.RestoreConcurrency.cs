namespace MachineConnectionApi.Tests;

using System.Net;
using System.Text.Json;
using MachineConnectionApi.Controllers;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class DeviceUpsertRegressionTests
{
    private static readonly TimeSpan RestoreTestTimeout = TimeSpan.FromSeconds(5);

    private static async Task RestoreOverlappingCreateKeepsOneDevice()
    {
        var store = new MemoryDeviceStore();
        var postWritten = RestoreSignal();
        var postReturn = RestoreSignal();
        MachineDeviceDto? remote = null;
        using var handler = new RestoreTestHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                remote = CredentialDevice() with { Id = payload.RootElement.GetProperty("id").GetString()! };
                postWritten.SetResult(true);
                await postReturn.Task.WaitAsync(RestoreTestTimeout, ct);
                return RestoreReply(remote, HttpStatusCode.Created);
            }
            if (request.RequestUri!.AbsolutePath == "/api/Devices")
                return RestoreReply(remote is null ? Array.Empty<MachineDeviceDto>() : new[] { remote });
            return RestoreReply(remote, remote is null ? HttpStatusCode.NotFound : HttpStatusCode.OK);
        });
        using var client = RestoreTestClient(handler);
        var creating = RestoreController(client, store).Create(new MachineDeviceUpsertRequest
        {
            Name = "Concurrent create", Type = "CNC", Protocol = "OpcUa", Host = "unused.invalid",
            Port = 4840, Password = "created-secret",
        }, CancellationToken.None);
        await postWritten.Task.WaitAsync(RestoreTestTimeout);
        var syncing = RestoreService(client, store).SyncAllAsync(CancellationToken.None);
        postReturn.SetResult(true);
        await Task.WhenAll(creating, syncing).WaitAsync(RestoreTestTimeout);
        var rows = store.ReadAll();
        Expect(rows.Count == 1 && rows[0].Id == remote?.Id, "Concurrent create/restore must retain exactly one copy of the device");
        Expect(rows[0].Password == "created-secret", "Restore must preserve credentials from the concurrent local creation");
        Expect(GetOkValue(await creating).Id == rows[0].Id, "The successfully created device must remain registered");
    }

    private static async Task RestoreOverlappingDeleteKeepsDeviceDeleted()
    {
        var device = CredentialDevice();
        var store = new MemoryDeviceStore(device);
        var deleteEntered = RestoreSignal();
        var deleteReturn = RestoreSignal();
        var remoteExists = true;
        using var handler = new RestoreTestHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                deleteEntered.SetResult(true);
                await deleteReturn.Task.WaitAsync(RestoreTestTimeout, ct);
                remoteExists = false;
                return RestoreReply(null, HttpStatusCode.NoContent);
            }
            return RestoreReply(remoteExists ? new[] { device } : Array.Empty<MachineDeviceDto>());
        });
        using var client = RestoreTestClient(handler);
        var deleting = RestoreController(client, store).Delete(device.Id, CancellationToken.None);
        await deleteEntered.Task.WaitAsync(RestoreTestTimeout);
        var syncing = RestoreService(client, store).SyncAllAsync(CancellationToken.None);
        deleteReturn.SetResult(true);
        await Task.WhenAll(deleting, syncing).WaitAsync(RestoreTestTimeout);
        Expect(await deleting is NoContentResult, "The concurrent delete must succeed");
        Expect(store.ReadAll().Count == 0 && !remoteExists, "Restore must not resurrect the successfully deleted last device");
    }

    private static async Task DeleteAfterRestoreSnapshotKeepsDeviceDeleted()
    {
        var device = CredentialDevice();
        var store = new MemoryDeviceStore();
        var snapshotReady = RestoreSignal();
        var snapshotReturn = RestoreSignal();
        var remoteExists = true;
        using var handler = new RestoreTestHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                remoteExists = false;
                return RestoreReply(null, HttpStatusCode.NoContent);
            }
            var snapshot = remoteExists ? new[] { device } : Array.Empty<MachineDeviceDto>();
            snapshotReady.SetResult(true);
            await snapshotReturn.Task.WaitAsync(RestoreTestTimeout, ct);
            return RestoreReply(snapshot);
        });
        using var client = RestoreTestClient(handler);
        var syncing = RestoreService(client, store).SyncAllAsync(CancellationToken.None);
        await snapshotReady.Task.WaitAsync(RestoreTestTimeout);
        var deleting = RestoreController(client, store).Delete(device.Id, CancellationToken.None);
        snapshotReturn.SetResult(true);
        await Task.WhenAll(syncing, deleting).WaitAsync(RestoreTestTimeout);
        Expect(await deleting is NoContentResult, "A delete queued after a restore snapshot must remove the restored device");
        Expect(store.ReadAll().Count == 0 && !remoteExists, "A stale restore snapshot must not survive the subsequent delete");
    }

    private static TaskCompletionSource<bool> RestoreSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage RestoreReply(object? value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private static HttpClient RestoreTestClient(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://restore.fixture/") };
    private static DevicesController RestoreController(HttpClient client, IDeviceStore store) => new(
        new CredentialClientFactory(client), new ConfigurationBuilder().Build(), store,
        RestoreService(client, store), new StubActivityLog(), NullLogger<DevicesController>.Instance);

    private sealed class RestoreTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
}
