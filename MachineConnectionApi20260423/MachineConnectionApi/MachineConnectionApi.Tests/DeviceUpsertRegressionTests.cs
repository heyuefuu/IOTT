namespace MachineConnectionApi.Tests;

using System.Text.Json;
using MachineConnectionApi.Controllers;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class DeviceUpsertRegressionTests
{
    public static async Task RunAll()
    {
        RequestWithoutServerFieldsDeserializes();
        await CreateGeneratesServerFields();
        await PartialUpdatePreservesServerFields();
    }

    private static void RequestWithoutServerFieldsDeserializes()
    {
        const string json = """{"name":"PLC-1","type":"PLC","protocol":"ModbusTCP","host":"127.0.0.1","port":502}""";
        var request = JsonSerializer.Deserialize<MachineDeviceUpsertRequest>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Expect(request is { Name: "PLC-1", Port: 502 }, "请求体应能在缺少服务端字段时反序列化");
        Expect(typeof(MachineDeviceUpsertRequest).GetProperty("Id") is null, "请求体不应暴露 Id");
        Expect(typeof(MachineDeviceUpsertRequest).GetProperty("Status") is null, "请求体不应暴露 Status");
        Expect(typeof(MachineDeviceUpsertRequest).GetProperty("CreatedAt") is null, "请求体不应暴露 CreatedAt");
    }

    private static async Task CreateGeneratesServerFields()
    {
        var store = new MemoryDeviceStore();
        var controller = CreateController(store);
        var startedAt = DateTimeOffset.Now;
        var result = await controller.Create(new MachineDeviceUpsertRequest
        {
            Name = "PLC-1", Type = "PLC", Protocol = "ModbusTCP", Host = "127.0.0.1", Port = 502,
        }, CancellationToken.None);
        var item = GetOkValue(result);

        Expect(item.Id.Length == 32, "Create 应生成设备 Id");
        Expect(item.Status == "Offline", "Create 应生成 Offline 状态");
        Expect(item.CreatedAt >= startedAt, "Create 应生成 CreatedAt");
        Expect(store.ReadAll().Single().Id == item.Id, "Create 应保存生成后的设备");
    }

    private static async Task PartialUpdatePreservesServerFields()
    {
        var createdAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var existing = new MachineDeviceDto
        {
            Id = "device-1",
            Name = "Old",
            Type = "PLC",
            Brand = "Inovance",
            Model = "H5U",
            Protocol = "ModbusTCP",
            Status = "Online",
            Host = "10.0.0.8",
            Port = 502,
            CreatedAt = createdAt,
            ExtendedProperties = new() { ["station"] = "1" },
        };
        var store = new MemoryDeviceStore(existing);
        var controller = CreateController(store);
        var result = await controller.Update(existing.Id,
            new MachineDeviceUpsertRequest { Name = "New" }, CancellationToken.None);
        var item = GetOkValue(result);

        Expect(item.Name == "New", "PUT 应更新已提供字段");
        Expect(item.Id == existing.Id && item.CreatedAt == createdAt, "PUT 应保留 Id 和 CreatedAt");
        Expect(item.Status == "Online", "PUT 应保留服务端状态");
        Expect(item.Host == existing.Host && item.Port == existing.Port, "PUT 应保留未提供字段");
        Expect(item.ExtendedProperties["station"] == "1", "PUT 应保留扩展属性");
    }

    private static DevicesController CreateController(IDeviceStore store) => new(
        new StubHttpClientFactory(),
        new ConfigurationBuilder().Build(),
        store,
        new StubSyncService(),
        new StubActivityLog(),
        NullLogger<DevicesController>.Instance);

    private static MachineDeviceDto GetOkValue(ActionResult<MachineDeviceDto> result) =>
        (result.Result as OkObjectResult)?.Value as MachineDeviceDto
        ?? throw new InvalidOperationException("Expected OkObjectResult with MachineDeviceDto");

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class MemoryDeviceStore(params MachineDeviceDto[] initial) : IDeviceStore
    {
        private readonly List<MachineDeviceDto> _items = [.. initial];

        public List<MachineDeviceDto> ReadAll() => [.. _items];

        public void WriteAll(IEnumerable<MachineDeviceDto> items)
        {
            _items.Clear();
            _items.AddRange(items);
        }

        public TResult Update<TResult>(Func<List<MachineDeviceDto>, TResult> update) => update(_items);
    }

    private sealed class StubSyncService : IDeviceUpstreamSyncService
    {
        public Task<UpstreamSyncResult> UpsertAsync(MachineDeviceDto device, CancellationToken ct) =>
            Task.FromResult(UpstreamSyncResult.Ok("updated"));

        public Task<UpstreamSyncResult> DeleteAsync(string deviceId, CancellationToken ct) =>
            Task.FromResult(UpstreamSyncResult.Ok("deleted"));

        public Task<UpstreamSyncReport> SyncAllAsync(CancellationToken ct) =>
            Task.FromResult(new UpstreamSyncReport());
    }

    private sealed class StubActivityLog : ISystemActivityLog
    {
        public List<SystemLogDto> ReadAll() => [];

        public SystemLogDto Append(SystemLogDto entry) => entry;

        public void Write(string type, string action, string detail, string user = "系统", string ip = "-") { }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}