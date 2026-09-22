namespace MachineConnectionApi.Tests;

using System.Text.Json;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class DeviceUpsertRegressionTests
{
    private static async Task ClearTransferRemovesIndependentChannel()
    {
        string[] bodies =
        [
            """{"clearTransfer":true}""",
            """{"clearTransfer":true,"transfer":{"protocol":"FTP","host":"127.0.0.1","port":21,"username":"ftp-user"}}""",
            """{"clearTransfer":true,"extendedProperties":{"DeviceId":"machine-sn"}}""",
        ];
        foreach (var body in bodies)
        {
            var request = JsonSerializer.Deserialize<MachineDeviceUpsertRequest>(body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Expect(request.ClearTransfer, "The HTTP request must deserialize clearTransfer");
            var existing = CredentialDevice();
            existing.ExtendedProperties["transferDeviceId"] = "old-file-device";
            var store = new MemoryDeviceStore(existing);
            var controller = CreateController(store);
            var response = GetOkValue(await controller.Update(existing.Id, request, CancellationToken.None));
            var saved = store.ReadAll().Single();
            Expect(saved.Transfer is null && response.Transfer is null,
                "Explicit clearing must remove the channel even when a redacted transfer is also supplied");
            if (request.ExtendedProperties is not null)
                Expect(!saved.ExtendedProperties.ContainsKey("transferDeviceId"),
                    "The form must be able to remove legacy linked-device routing when cancelling its file channel");
            Expect(saved.Password == existing.Password && saved.Protocol == existing.Protocol,
                "Clearing a transfer must preserve the primary connection");
            var created = GetOkValue(await CreateController(new MemoryDeviceStore()).Create(request with
            {
                Name = existing.Name, Type = existing.Type, Protocol = existing.Protocol,
                Host = existing.Host, Port = existing.Port,
            }, CancellationToken.None));
            Expect(created.Transfer is null, "ClearTransfer must also take precedence during creation");
        }
    }

    private static async Task PartialUpdatesKeepIndependentChannel()
    {
        string[] bodies =
        [
            """{"name":"Renamed"}""",
            """{"name":"Renamed","clearTransfer":false}""",
            """{"name":"Renamed","transfer":null}""",
        ];
        foreach (var body in bodies)
        {
            var request = JsonSerializer.Deserialize<MachineDeviceUpsertRequest>(body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Expect(!request.ClearTransfer, "Omitted clearTransfer must default to false");
            var existing = CredentialDevice();
            var store = new MemoryDeviceStore(existing);
            var response = GetOkValue(await CreateController(store).Update(existing.Id, request, CancellationToken.None));
            var saved = store.ReadAll().Single();
            Expect(saved.Name == "Renamed" && saved.Transfer == existing.Transfer,
                "Partial updates must retain the independent channel and its credentials");
            Expect(response.Transfer is { Password: null }, "Partial update responses must retain credential redaction");
        }
    }

    private static async Task UpstreamSyncClearsRemovedTransfer()
    {
        var existing = CredentialDevice();
        var cleared = existing with { Transfer = null };
        using var handler = new CredentialSyncHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var service = new DeviceUpstreamSyncService(new CredentialClientFactory(client), new MemoryDeviceStore(cleared),
            new ConfigurationBuilder().Build(), NullLogger<DeviceUpstreamSyncService>.Instance);
        Expect((await service.UpsertAsync(cleared, CancellationToken.None)).Success, "Creating without a channel must succeed");
        Expect((await service.UpsertAsync(cleared, CancellationToken.None)).Success, "Synchronizing a removed channel must succeed");
        Expect((await service.UpsertAsync(existing, CancellationToken.None)).Success, "Synchronizing a configured channel must succeed");
        Expect(handler.Payloads.Count == 3, "Every synchronization must send a payload");
        Expect(!handler.Payloads[0].TryGetProperty("clearTransfer", out _) &&
            !handler.Payloads[0].TryGetProperty("transfer", out _), "Creation must omit the update-only clearing flag");
        Expect(handler.Payloads[1].GetProperty("clearTransfer").GetBoolean() &&
            !handler.Payloads[1].TryGetProperty("transfer", out _), "A mirrored update without a channel must explicitly clear it in Host");
        Expect(!handler.Payloads[2].TryGetProperty("clearTransfer", out _) &&
            handler.Payloads[2].GetProperty("transfer").GetProperty("password").GetString() == existing.Transfer!.Password,
            "A configured channel must reach Host with its credentials and without a clearing flag");
    }
}
