namespace MachineConnectionApi.Tests;

using System.Net;
using System.Text.Json;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class DeviceUpsertRegressionTests
{
    private static async Task CredentialsAreStoredAndRedacted()
    {
        const string json = """
            {"name":"CNC-1","type":"CNC","brand":"HNC","model":"HNC-848Di-17",
             "protocol":"OpcUa","host":"127.0.0.1","port":4840,"username":"opc-user","password":"opc-secret",
             "transfer":{"protocol":"FTP","host":"127.0.0.1","port":21,"username":"ftp-user","password":"ftp-secret"}}
            """;
        var request = JsonSerializer.Deserialize<MachineDeviceUpsertRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var store = new MemoryDeviceStore();
        var controller = CreateController(store);
        var response = GetOkValue(await controller.Create(request, CancellationToken.None));
        var stored = store.ReadAll().Single();
        Expect(stored.Password == "opc-secret" && stored.Transfer?.Password == "ftp-secret", "Create must retain both passwords internally");
        Expect(response.Password is null && response.Transfer?.Password is null, "Create must redact passwords");
        var detail = GetOkValue(controller.GetById(stored.Id));
        var list = (controller.List(null).Result as OkObjectResult)?.Value as IEnumerable<MachineDeviceDto>;
        Expect(detail.Password is null && detail.Transfer?.Password is null, "Get must redact passwords");
        Expect(list?.Single() is { Password: null, Transfer.Password: null }, "List must redact passwords");
        var fileName = $"credential-test-{Guid.NewGuid():N}.json";
        try
        {
            new JsonFileStore<MachineDeviceDto>(fileName).WriteAll([stored]);
            var persisted = new JsonFileStore<MachineDeviceDto>(fileName).ReadAll().Single();
            Expect(persisted.Password == stored.Password && persisted.Transfer?.Password == stored.Transfer?.Password,
                "Passwords must survive a store reload");
        }
        finally { File.Delete(Path.Combine(AppContext.BaseDirectory, "App_Data", fileName)); }
    }

    private static async Task CredentialEditsPreserveOrReplacePasswords()
    {
        var device = CredentialDevice();
        var store = new MemoryDeviceStore(device);
        var controller = CreateController(store);
        var visible = GetOkValue(controller.GetById(device.Id));
        var response = GetOkValue(await controller.Update(device.Id,
            new MachineDeviceUpsertRequest { Name = "Renamed", Transfer = visible.Transfer }, CancellationToken.None));
        var saved = store.ReadAll().Single();
        Expect(saved.Password == device.Password && saved.Transfer?.Password == device.Transfer?.Password,
            "Editing with omitted passwords must keep both stored credentials");
        Expect(response.Password is null && response.Transfer?.Password is null, "Update must redact passwords");
        await controller.Update(device.Id, new MachineDeviceUpsertRequest
        {
            Password = "new-opc-secret", Transfer = device.Transfer! with { Password = "new-ftp-secret" },
        }, CancellationToken.None);
        saved = store.ReadAll().Single();
        Expect(saved.Password == "new-opc-secret" && saved.Transfer?.Password == "new-ftp-secret", "Explicit passwords must replace old values");
        await controller.Update(device.Id, new MachineDeviceUpsertRequest
        {
            Transfer = visible.Transfer! with { Host = "127.0.0.2" },
        }, CancellationToken.None);
        Expect(store.ReadAll().Single().Transfer?.Password is null, "A different FTP endpoint must not inherit a hidden password");
        await controller.Update(device.Id, new MachineDeviceUpsertRequest
        {
            Password = "", Transfer = device.Transfer! with { Password = "" },
        }, CancellationToken.None);
        saved = store.ReadAll().Single();
        Expect(saved.Password == "" && saved.Transfer?.Password == "", "Explicit empty passwords must be preserved");
    }

    private static async Task UpstreamSyncSendsBothPasswords()
    {
        var device = CredentialDevice();
        using var handler = new CredentialSyncHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var service = new DeviceUpstreamSyncService(new CredentialClientFactory(client), new MemoryDeviceStore(device),
            new ConfigurationBuilder().Build(), NullLogger<DeviceUpstreamSyncService>.Instance);
        Expect((await service.UpsertAsync(device, CancellationToken.None)).Success, "Upstream create must succeed");
        Expect((await service.UpsertAsync(device, CancellationToken.None)).Success, "Upstream update must succeed");
        Expect(handler.Payloads.Count == 2, "Both create and update must send a payload");
        foreach (var payload in handler.Payloads)
        {
            Expect(payload.GetProperty("password").GetString() == device.Password, "OPC UA password must reach the Host");
            Expect(payload.GetProperty("transfer").GetProperty("password").GetString() == device.Transfer?.Password,
                "FTP password must reach the Host independently");
            Expect(payload.GetProperty("protocol").GetString() == "OpcUa", "FTP must not replace the primary protocol");
        }
    }

    private static MachineDeviceDto CredentialDevice() => new()
    {
        Id = "credential-device", Name = "CNC-1", Type = "CNC", Brand = "HNC", Model = "HNC-848Di-17",
        Protocol = "OpcUa", Status = "Offline", Host = "127.0.0.1", Port = 4840,
        Username = "opc-user", Password = "opc-secret", CreatedAt = DateTimeOffset.UtcNow,
        Transfer = new TransferDeviceDto
        {
            Protocol = "FTP", Host = "127.0.0.1", Port = 21, Username = "ftp-user", Password = "ftp-secret",
        },
    };

    private sealed class CredentialClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CredentialSyncHandler : HttpMessageHandler
    {
        public List<JsonElement> Payloads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(Payloads.Count == 0 ? HttpStatusCode.NotFound : HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Payloads.Add(json.RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
