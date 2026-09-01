namespace IndustrialIoT.Host.Controllers;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Host.Services;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class AddressSpaceController : ControllerBase
{
    private readonly IPooledDriverAccessor _drivers;
    private readonly IDeviceRepository _deviceRepo;
    private readonly IAddressSpaceBrowseService _browseService;

    public AddressSpaceController(
        IPooledDriverAccessor drivers,
        IDeviceRepository deviceRepo,
        IAddressSpaceBrowseService browseService)
    {
        _drivers = drivers;
        _deviceRepo = deviceRepo;
        _browseService = browseService;
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
            return BadRequest("Device driver does not support address space browsing");

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
            return BadRequest("Device driver does not support address space browsing");

        var contentType = format switch
        {
            ExportFormat.CSV => "text/csv",
            ExportFormat.JSON => "application/json",
            _ => "application/octet-stream"
        };
        return File(outcome.Value, contentType, $"address_space_{deviceId}.{format.ToString().ToLower()}");
    }
}
