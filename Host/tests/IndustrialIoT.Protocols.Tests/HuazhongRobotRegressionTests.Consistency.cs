using System.Text;
using System.Text.Json;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Host.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class HuazhongRobotRegressionTests
{
    private static async Task ImportConsistencyAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var device = new Device { Id = "concurrent-import", Name = "Robot", Type = DeviceType.Robot,
            Brand = "HSR", Model = "HSR", Protocol = ProtocolType.HuazhongRobot,
            ConnectionConfig = new() { Host = "127.0.0.1", Port = 1 } };
        var devices = new ImportDeviceRepository(device);
        var profiles = new ImportProfileRepository();
        var pool = new ImportConnectionPool();
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var imports = new ImportRepository(devices, profiles)
        {
            BeforeSave = async () => { saving.TrySetResult(); await release.Task.WaitAsync(timeout.Token); },
        };
        BatchImportController Controller() => new(devices, profiles,
            NullLogger<BatchImportController>.Instance, pool, new(), imports);
        var first = ImportPointAsync(Controller(), device.Id, "First", timeout.Token);
        await saving.Task.WaitAsync(timeout.Token);
        var second = ImportPointAsync(Controller(), device.Id, "Second", timeout.Token);
        using var canceled = new CancellationTokenSource();
        var waiting = ImportPointAsync(Controller(), device.Id, "Canceled", canceled.Token);
        canceled.Cancel();
        try { await waiting; throw new InvalidOperationException("Canceled import was accepted"); }
        catch (OperationCanceledException) { }
        try
        {
            TestSupport.Require(devices.Reads == 1 && pool.Released is null,
                "Concurrent import read a stale map or released the connection before commit");
        }
        finally { release.TrySetResult(); }
        var results = await Task.WhenAll(first, second);
        TestSupport.Require(results.All(result => result.SuccessCount == 1) && profiles.AddCalls == 2,
            "Concurrent imports did not both finish");
        var paths = JsonDocument.Parse(device.ConnectionConfig.ExtendedProperties["AddressMap"])
            .RootElement.EnumerateArray().Select(node => node.GetProperty("Path").GetString()).ToArray();
        TestSupport.Require(paths.Contains("/Robot/First") && paths.Contains("/Robot/Second") && paths.Length == 2,
            "Concurrent import lost a successful point or saved a canceled point");

        var original = device.ConnectionConfig;
        imports.FailSave = true;
        pool = new ImportConnectionPool();
        try { await ImportPointAsync(Controller(), device.Id, "Failed", timeout.Token); }
        catch (InvalidOperationException ex) when (ex.Message == "Injected save failure") { }
        TestSupport.Require(ReferenceEquals(original, device.ConnectionConfig) && profiles.AddCalls == 2
            && pool.Released is null, "Failed import changed the device, profile or live connection");
        imports.FailSave = false;
        TestSupport.Require((await ImportPointAsync(Controller(), device.Id, "Retry", timeout.Token)).SuccessCount == 1,
            "Failed import did not release its device lock");
    }

    private static async Task<BatchImportResult> ImportPointAsync(BatchImportController controller,
        string deviceId, string name, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new[] { new { address = "/Robot/" + name,
            modbusAddress = "240", dataType = "UInt16", groupName = "Main", intervalMs = 1000 } });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var file = new FormFile(stream, 0, stream.Length, "file", "tags.json");
        return (BatchImportResult)((OkObjectResult)(await controller.ImportTags(deviceId, file, ct)).Result!).Value!;
    }

    private sealed class ImportRepository(ImportDeviceRepository devices, ImportProfileRepository profiles)
        : ICollectionImportRepository
    {
        public Func<Task>? BeforeSave { get; init; }
        public bool FailSave { get; set; }
        public async Task SaveAsync(Device device, DeviceConnectionConfig config,
            CollectionProfile profile, CancellationToken ct = default)
        {
            if (BeforeSave is not null) await BeforeSave();
            if (FailSave) throw new InvalidOperationException("Injected save failure");
            device.ConnectionConfig = config;
            await devices.UpdateAsync(device, ct);
            await profiles.AddAsync(profile, ct);
        }
    }
}
