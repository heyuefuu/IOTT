namespace IndustrialIoT.Host.Controllers;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Host.Services;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class AddressSpaceController : ControllerBase
{
    private readonly IPooledDriverAccessor _drivers;
    private readonly IDeviceRepository _deviceRepo;
    private readonly IAddressSpaceBrowseService _browseService;
    private readonly IDriverRegistry _registry;

    public AddressSpaceController(
        IPooledDriverAccessor drivers,
        IDeviceRepository deviceRepo,
        IAddressSpaceBrowseService browseService,
        IDriverRegistry registry)
    {
        _drivers = drivers;
        _deviceRepo = deviceRepo;
        _browseService = browseService;
        _registry = registry;
    }

    /// <summary>浏览设备地址空间</summary>
    [HttpGet("{deviceId}")]
    public async Task<ActionResult<IReadOnlyList<AddressNode>>> Browse(
        string deviceId,
        [FromQuery] string? parentPath = null,
        CancellationToken ct = default)
    {
        var device = await _deviceRepo.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound();

        var driverType = _registry.Resolve(device.Protocol, device.Brand, device.Model);
        if (driverType is not null && !typeof(IAddressSpaceBrowser).IsAssignableFrom(driverType))
            return BrowsingNotSupported(device.Protocol);

        // Reuse the pooled long-lived connection — see PooledDriverAccessor
        var outcome = await _drivers.ExecuteAsync<IReadOnlyList<AddressNode>?>(
            deviceId,
            async driver =>
            {
                if (driver is not IAddressSpaceBrowser browser)
                    return null;

                return await _browseService.BrowseAsync(
                    browser,
                    parentPath,
                    recursive: device.Protocol == ProtocolType.FOCAS,
                    ct: ct);
            },
            ct);

        if (!outcome.Success)
            return StatusCode(502, new { error = $"Browse failed: {outcome.ErrorMessage}" });
        if (outcome.Value is null)
            return BrowsingNotSupported(device.Protocol);

        return Ok(outcome.Value);
    }

    /// <summary>导出设备地址空间</summary>
    [HttpGet("{deviceId}/export")]
    public async Task<IActionResult> Export(
        string deviceId,
        [FromQuery] ExportFormat format = ExportFormat.CSV,
        CancellationToken ct = default)
    {
        var device = await _deviceRepo.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound();

        var driverType = _registry.Resolve(device.Protocol, device.Brand, device.Model);
        if (driverType is not null && !typeof(IAddressSpaceBrowser).IsAssignableFrom(driverType))
            return BrowsingNotSupported(device.Protocol);

        // Reuse the pooled long-lived connection — see PooledDriverAccessor
        var outcome = await _drivers.ExecuteAsync<Stream?>(
            deviceId,
            async driver => driver is IAddressSpaceBrowser browser
                ? await browser.ExportAddressSpaceAsync(format, ct)
                : null,
            ct);

        if (!outcome.Success)
            return StatusCode(502, new { error = $"Browse export failed: {outcome.ErrorMessage}" });
        if (outcome.Value is null)
            return BrowsingNotSupported(device.Protocol);

        var contentType = format switch
        {
            ExportFormat.CSV => "text/csv",
            ExportFormat.JSON => "application/json",
            _ => "application/octet-stream"
        };
        return File(outcome.Value, contentType, $"address_space_{deviceId}.{format.ToString().ToLower()}");
    }

    private ObjectResult BrowsingNotSupported(ProtocolType protocol) => StatusCode(
        StatusCodes.Status422UnprocessableEntity,
        new
        {
            code = "ADDRESS_SPACE_BROWSING_NOT_SUPPORTED",
            error = $"当前 {protocol} 驱动未实现在线地址枚举，不会生成预设点位。可按设备实际地址手动配置采集，无需导入文件；如设备支持 OPC UA，可使用 OPC UA 在线浏览。",
        });
}
