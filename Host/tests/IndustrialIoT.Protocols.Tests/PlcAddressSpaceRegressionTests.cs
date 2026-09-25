using System.Text.Json;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Host;
using IndustrialIoT.Host.Controllers;
using IndustrialIoT.Host.Services;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Registration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

internal static class PlcAddressSpaceRegressionTests
{
    public static async Task RunAsync()
    {
        var registry = new DriverRegistry();
        var services = new ServiceCollection();
        services.AddLogging();
        ProtocolDriverRegistration.RegisterRealDrivers(services, registry);
        await using var provider = services.BuildServiceProvider();
        var protocols = new (ProtocolType Protocol, string Brand)[]
        {
            (ProtocolType.SiemensS7, "Siemens"),
            (ProtocolType.ModbusTCP, "PLC"),
            (ProtocolType.ModbusRTU, "PLC"),
            (ProtocolType.Profibus, "Profibus"),
            (ProtocolType.Inovance, "Inovance"),
            (ProtocolType.InovanceSerial, "Inovance"),
            (ProtocolType.InovanceSerialOverTcp, "Inovance"),
            (ProtocolType.FINS, "Omron"),
            (ProtocolType.OmronHostLink, "Omron"),
            (ProtocolType.Mewtocol, "Panasonic"),
            (ProtocolType.MewtocolSerial, "Panasonic"),
        };
        foreach (var (protocol, brand) in protocols)
        {
            var driverType = registry.Resolve(protocol, brand)!;
            var driver = (IProtocolDriver)provider.GetRequiredService(driverType);
            TestSupport.Require(driver is not IAddressSpaceBrowser, $"{protocol} still exposes synthetic browsing");
            TestSupport.Require(!driver.Capabilities.HasFlag(DriverCapabilities.Browse), $"{protocol} advertises unsupported browsing");
            var readWrite = DriverCapabilities.Read | DriverCapabilities.Write | DriverCapabilities.BatchRead;
            TestSupport.Require((driver.Capabilities & readWrite) == readWrite, $"{protocol} lost real read/write capabilities");
            var device = CreateDevice(protocol, brand);
            var accessor = new OfflineAccessor();
            var controller = new AddressSpaceController(accessor, new DeviceRepository(device), new AddressSpaceBrowseService(), registry);
            foreach (var parentPath in new string?[] { null, "I", "DB1" })
                RequireUnsupported((await controller.Browse(device.Id, parentPath)).Result, protocol);
            foreach (var format in new[] { ExportFormat.CSV, ExportFormat.JSON, ExportFormat.Excel })
                RequireUnsupported(await controller.Export(device.Id, format), protocol);
            TestSupport.Require(accessor.Calls == 0, $"{protocol} attempted a device connection just to reject unsupported browsing");
            TestSupport.Require((await controller.Browse("missing")).Result is NotFoundResult, "Unknown device must remain 404");
        }

        var onlineDevice = CreateDevice(ProtocolType.OpcUa, "Siemens");
        var onlineType = registry.Resolve(onlineDevice.Protocol, onlineDevice.Brand)!;
        TestSupport.Require(typeof(IAddressSpaceBrowser).IsAssignableFrom(onlineType), "OPC UA lost its real browser");
        var onlineAccessor = new OfflineAccessor();
        var onlineController = new AddressSpaceController(onlineAccessor, new DeviceRepository(onlineDevice), new AddressSpaceBrowseService(), registry);
        TestSupport.Require((await onlineController.Browse(onlineDevice.Id)).Result is ObjectResult { StatusCode: 502 },
            "Online-capable driver must attempt the real connection and report its actual failure");
        TestSupport.Require(await onlineController.Export(onlineDevice.Id) is ObjectResult { StatusCode: 502 },
            "Online-capable export must preserve its real connection path");
        TestSupport.Require(onlineAccessor.Calls == 2, "OPC UA browsing or export was blocked before the driver");
    }

    private static void RequireUnsupported(IActionResult? result, ProtocolType protocol)
    {
        TestSupport.Require(result is ObjectResult { StatusCode: 422 }, $"{protocol} did not report unsupported browsing");
        var payload = JsonSerializer.SerializeToElement(((ObjectResult)result!).Value);
        TestSupport.Require(payload.GetProperty("code").GetString() == "ADDRESS_SPACE_BROWSING_NOT_SUPPORTED",
            $"{protocol} did not explain why no real address directory is available");
    }

    private static Device CreateDevice(ProtocolType protocol, string brand) => new()
    {
        Id = protocol.ToString(), Name = "PLC browsing fixture", Type = DeviceType.PLC,
        Protocol = protocol, Brand = brand, Model = "",
        ConnectionConfig = new() { Host = "127.0.0.1", Port = 1 },
    };

    private sealed class OfflineAccessor : IPooledDriverAccessor
    {
        public int Calls { get; private set; }
        public Task<PooledDriverResult<T>> ExecuteAsync<T>(string deviceId, Func<IProtocolDriver, Task<T>> action, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new PooledDriverResult<T>(false, default, "device offline"));
        }
    }

    private sealed class DeviceRepository(Device device) : IDeviceRepository
    {
        public Task<Device?> GetByIdAsync(string deviceId, CancellationToken ct = default) =>
            Task.FromResult(deviceId == device.Id ? device : null);
        public Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Device>>([device]);
        public Task<IReadOnlyList<Device>> GetByTypeAsync(DeviceType type, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Device>>(device.Type == type ? [device] : []);
        public Task AddAsync(Device added, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Device updated, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string deviceId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
