namespace MachineConnectionApi.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

internal static partial class DeviceUpsertRegressionTests
{
    private static async Task SeedInitializesRegistryWhenUpstreamIsEmpty()
    {
        var seed = CredentialDevice() with
        {
            Status = "Online", UpstreamSynced = true, UpstreamError = "old", RestoredFromUpstream = true,
            ExtendedProperties = new() { ["DeviceCode"] = "h001" },
        };
        var seedPath = WriteSeed(JsonSerializer.Serialize(new[] { seed }, DeviceSnapshot.JsonOptions));
        try
        {
            var store = new MemoryDeviceStore();
            using var handler = new SeedUpstreamHandler("[]");
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var report = await RestoreService(client, store, seedPath).SyncAllAsync(CancellationToken.None);
            var saved = store.ReadAll().Single();
            Expect(report is { Seeded: 1, Created: 1, Failed: 0 }, "An empty gateway and upstream must load and publish the seed");
            Expect(saved.Id == seed.Id && saved.ExtendedProperties["DeviceCode"] == "h001" && saved.Transfer?.Host == seed.Transfer?.Host,
                "Seeding must keep device identity, extension fields and the transfer channel");
            Expect(saved.Password == seed.Password && saved.Transfer?.Password == seed.Transfer?.Password,
                "Seeding must keep credentials supplied by the seed file");
            Expect(saved is { Status: "Offline", RestoredFromUpstream: false, UpstreamSynced: true, UpstreamError: null },
                "Seeding must drop runtime state from the source machine");
            Expect(handler.Posted.Single() == seed.Id, "Seeded devices must be created upstream");
        }
        finally { File.Delete(seedPath); }
    }

    private static async Task SeedIsIgnoredWhenUpstreamHasDevices()
    {
        var remote = CredentialDevice() with { Id = "remote-device", Name = "Remote" };
        var seedPath = WriteSeed(JsonSerializer.Serialize(new[] { CredentialDevice() }, DeviceSnapshot.JsonOptions));
        try
        {
            var store = new MemoryDeviceStore();
            using var handler = new SeedUpstreamHandler(JsonSerializer.Serialize(new[] { remote }));
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var report = await RestoreService(client, store, seedPath).SyncAllAsync(CancellationToken.None);
            Expect(report is { Restored: 1, Seeded: 0 }, "Upstream devices must take priority over the seed");
            Expect(store.ReadAll().Single().Id == remote.Id, "The seed must not be mixed into a restored registry");
            Expect(handler.Posted.Count == 0, "Restoring from upstream must not write upstream");
        }
        finally { File.Delete(seedPath); }
    }

    private static async Task InvalidSeedLeavesRegistryEmpty()
    {
        foreach (var body in new[] { "not-json", "[{}]", "[null]" })
        {
            var seedPath = WriteSeed(body);
            try
            {
                var store = new MemoryDeviceStore();
                using var handler = new SeedUpstreamHandler("[]");
                using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
                var report = await RestoreService(client, store, seedPath).SyncAllAsync(CancellationToken.None);
                Expect(report is { Seeded: 0, Failed: 0 } && store.ReadAll().Count == 0,
                    "A corrupt seed file must be ignored without partially populating the registry");
            }
            finally { File.Delete(seedPath); }
        }
    }

    private static async Task ExportImportRoundTripKeepsFullConfiguration()
    {
        var source = CredentialDevice() with
        {
            Status = "Online", UpstreamSynced = false, UpstreamError = "old",
            ExtendedProperties = new() { ["MqttBrokerHost"] = "10.0.0.5" },
        };
        var export = CreateController(new MemoryDeviceStore(source)).Export() as FileContentResult
            ?? throw new InvalidOperationException("Export must return a file");
        Expect(export.ContentType == "application/json", "Export must be JSON");

        var target = new MemoryDeviceStore();
        var controller = CreateController(target);
        var first = GetImportResult(await controller.Import(JsonFormFile(export.FileContents), CancellationToken.None));
        var saved = target.ReadAll().Single();
        Expect(first is { Total: 1, Success: 1, Failed: 0 }, "Exported devices must import");
        Expect(saved.Id == source.Id && saved.ExtendedProperties["MqttBrokerHost"] == "10.0.0.5",
            "Import must keep the device ID and extension fields");
        Expect(saved.Password == source.Password && saved.Transfer?.Password == source.Transfer?.Password,
            "Import must keep both credentials from the export file");
        Expect(saved.Status == "Offline", "Import must not carry over the source machine's online state");

        var second = GetImportResult(await controller.Import(JsonFormFile(export.FileContents), CancellationToken.None));
        Expect(second is { Success: 0, Failed: 1 } && target.ReadAll().Count == 1,
            "Re-importing the same export must skip existing IDs instead of duplicating devices");

        var invalid = await controller.Import(JsonFormFile(Encoding.UTF8.GetBytes("[{}]")), CancellationToken.None);
        Expect(invalid.Result is BadRequestObjectResult && target.ReadAll().Count == 1,
            "An invalid JSON import must be rejected without changes");
    }

    private static string WriteSeed(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"device-seed-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static IFormFile JsonFormFile(byte[] content) =>
        new FormFile(new MemoryStream(content), 0, content.Length, "file", "devices.json");

    private static DeviceImportResult GetImportResult(ActionResult<DeviceImportResult> result) =>
        (result.Result as OkObjectResult)?.Value as DeviceImportResult
        ?? throw new InvalidOperationException("Expected OkObjectResult with DeviceImportResult");

    /// <summary>上游：设备列表返回指定内容，单设备探测 404，创建记录 ID。</summary>
    private sealed class SeedUpstreamHandler(string listBody) : HttpMessageHandler
    {
        public List<string> Posted { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Posted.Add(json.RootElement.GetProperty("id").GetString()!);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return request.RequestUri!.AbsolutePath == "/api/Devices"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listBody) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
