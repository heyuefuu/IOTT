using System.Text.Json;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Host.Controllers;
using IndustrialIoT.Host.Services;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Addressing;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.AspNetCore.Mvc;

internal static partial class PlcAddressRangeRegressionTests
{
    public static async Task RunAsync(IDriverRegistry registry, IProtocolDriverFactory factory)
    {
        await PaginationAndExportAsync();
        ConfigurationValidation();
        var fixtures = new (ProtocolType Protocol, string Brand, string Model, string Area)[]
        {
            (ProtocolType.FINS, "Omron", "CP1E-N", "DM"),
            (ProtocolType.OmronHostLink, "Omron", "CP1H", "DM"),
            (ProtocolType.Mewtocol, "Panasonic", "FP0R-C32", "DT"),
            (ProtocolType.MewtocolSerial, "Panasonic", "FP0R-C16", "DT"),
            (ProtocolType.Inovance, "Inovance", "H3U", "D"),
            (ProtocolType.InovanceSerial, "Inovance", "H3U", "D"),
            (ProtocolType.InovanceSerialOverTcp, "Inovance", "H3U", "D"),
        };
        foreach (var fixture in fixtures)
        {
            var config = new DeviceConnectionConfig { Host = "127.0.0.1", Port = 1,
                ExtendedProperties = new() { ["Series"] = "H3U",
                    ["AddressSpaceRanges"] = JsonSerializer.Serialize(new[] { new { area = fixture.Area, start = 126, end = 129 } }) } };
            await using var driver = factory.Create(fixture.Protocol, fixture.Brand, fixture.Model);
            TestSupport.Require(driver is IAddressRangeBrowser, $"{fixture.Protocol} has no range browser");
            var capabilities = DriverCapabilities.Read | DriverCapabilities.Write | DriverCapabilities.BatchRead | DriverCapabilities.Browse;
            TestSupport.Require((driver.Capabilities & capabilities) == capabilities, $"{fixture.Protocol} lost capabilities");
            var browser = ((IAddressRangeBrowser)driver).CreateAddressSpaceBrowser(config, fixture.Model);
            var roots = await browser.BrowseAsync();
            TestSupport.Require(roots.Count == 1 && roots[0].Source == "ConfiguredRange" && !string.IsNullOrEmpty(roots[0].Description),
                "Configured catalogs must explain their origin");
            var leaves = await browser.BrowseAsync(fixture.Area);
            TestSupport.Require(leaves.Select(node => node.Path).SequenceEqual(Enumerable.Range(126, 4).Select(index => fixture.Area + index)),
                $"{fixture.Protocol} produced wrong wire addresses");
            TestSupport.Require(driver.State == ConnectionState.Disconnected, "Browsing metadata opened a connection");
            var device = new Device { Id = fixture.Protocol.ToString(), Name = "Range test", Type = DeviceType.PLC,
                Protocol = fixture.Protocol, Brand = fixture.Brand, Model = fixture.Model, ConnectionConfig = config };
            var accessor = new ForbiddenAccessor();
            var controller = new AddressSpaceController(accessor, new SingleDeviceRepository(device), new AddressSpaceBrowseService(), registry, factory);
            TestSupport.Require((await controller.Browse(device.Id)).Result is OkObjectResult, "Offline range browsing failed");
            foreach (var format in new[] { ExportFormat.CSV, ExportFormat.JSON })
            {
                var result = await controller.Export(device.Id, format);
                TestSupport.Require(result is FileStreamResult, "Range export should return a file without connecting");
                await using var stream = ((FileStreamResult)result).FileStream;
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync();
                TestSupport.Require(text.Contains(fixture.Area + "126") && text.Contains(fixture.Area + "129") && text.Contains("ConfiguredRange"),
                    "Export lost addresses or origin");
            }
            RequireError((await controller.Browse(device.Id, "missing")).Result, 400, "ADDRESS_SPACE_PATH_INVALID");
            RequireError(await controller.Export(device.Id, ExportFormat.Excel), 400, "ADDRESS_SPACE_EXPORT_FORMAT_NOT_SUPPORTED");
            device.ConnectionConfig = config with { ExtendedProperties = new() { ["AddressSpaceRanges"] = "invalid" } };
            RequireError((await controller.Browse(device.Id)).Result, 422, "ADDRESS_SPACE_CONFIGURATION_REQUIRED");
            TestSupport.Require(accessor.Calls == 0, "Metadata browse/export used the connection pool");
        }
        await ProfileBoundariesAsync(factory);
    }

    private static void RequireError(IActionResult? result, int status, string code)
    {
        TestSupport.Require(result is ObjectResult value && value.StatusCode == status, $"Expected HTTP {status}");
        var payload = JsonSerializer.SerializeToElement(((ObjectResult)result!).Value);
        TestSupport.Require(payload.GetProperty("code").GetString() == code, $"Expected error {code}");
    }

    private sealed class ForbiddenAccessor : IPooledDriverAccessor
    {
        public int Calls { get; private set; }
        public Task<PooledDriverResult<T>> ExecuteAsync<T>(string deviceId, Func<IProtocolDriver, Task<T>> action, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("A declared address catalog must not access the PLC");
        }
    }

    private sealed class SingleDeviceRepository(Device device) : IDeviceRepository
    {
        public Task<Device?> GetByIdAsync(string deviceId, CancellationToken ct = default) => Task.FromResult(deviceId == device.Id ? device : null);
        public Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Device>>([device]);
        public Task<IReadOnlyList<Device>> GetByTypeAsync(DeviceType type, CancellationToken ct = default) => GetAllAsync(ct);
        public Task AddAsync(Device added, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Device updated, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string deviceId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
