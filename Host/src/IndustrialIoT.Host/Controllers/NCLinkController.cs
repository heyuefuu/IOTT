namespace IndustrialIoT.Host.Controllers;

using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Protocols.NCLink;
using IndustrialIoT.Protocols.Registration;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// NC-Link 调试接口 — 直接暴露机床 Probe 自报的 DataItem 列表，
/// 让上层（前端/排查）能基于真实 ID 选点，不再依赖硬编码品牌字典。
/// </summary>
[ApiController]
[Route("api/nclink")]
public class NCLinkController : ControllerBase
{
    private readonly IProtocolDriverFactory _factory;
    private readonly IDeviceRepository _deviceRepo;
    private readonly ILogger<NCLinkController> _logger;

    public NCLinkController(
        IProtocolDriverFactory factory,
        IDeviceRepository deviceRepo,
        ILogger<NCLinkController> logger)
    {
        _factory = factory;
        _deviceRepo = deviceRepo;
        _logger = logger;
    }

    /// <summary>获取机床 Probe 自报的完整设备模型（原始结构）。</summary>
    [HttpGet("{deviceId}/probe")]
    public async Task<IActionResult> GetProbeModel(string deviceId, CancellationToken ct)
    {
        var device = await _deviceRepo.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound($"Device {deviceId} not found");

        await using var driver = _factory.Create(device.Protocol, device.Brand, device.Model);
        if (driver is not NCLinkDriver ncLink)
            return BadRequest($"Device protocol {device.Protocol} is not NC-Link");

        var connect = await ncLink.ConnectAsync(device.ConnectionConfig, ct);
        if (!connect.Success)
            return StatusCode(502, new { error = $"Connection failed: {connect.ErrorMessage}" });

        var model = ncLink.DeviceModel;
        if (model is null)
            return StatusCode(502, new { error = "Probe response missing — device did not return model" });

        return Ok(new
        {
            id = model.Id,
            guid = model.Guid,
            version = model.Version,
            dataItemCount = model.DataItems.Count,
            sampleChannelCount = model.SampleChannels.Count,
            dataItems = model.DataItems,
            sampleChannels = model.SampleChannels,
        });
    }

    /// <summary>获取机床 Probe 报出的 DataItem 扁平列表（仅 id/name/type/settable）。</summary>
    [HttpGet("{deviceId}/dataitems")]
    public async Task<IActionResult> GetDataItems(string deviceId, CancellationToken ct)
    {
        var device = await _deviceRepo.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound($"Device {deviceId} not found");

        await using var driver = _factory.Create(device.Protocol, device.Brand, device.Model);
        if (driver is not NCLinkDriver ncLink)
            return BadRequest($"Device protocol {device.Protocol} is not NC-Link");

        var connect = await ncLink.ConnectAsync(device.ConnectionConfig, ct);
        if (!connect.Success)
            return StatusCode(502, new { error = $"Connection failed: {connect.ErrorMessage}" });

        var model = ncLink.DeviceModel;
        if (model is null)
            return StatusCode(502, new { error = "Probe response missing" });

        var items = model.DataItems.Select(di => new
        {
            id = di.Id,
            name = di.Name,
            type = di.Type,
            settable = di.Settable,
            unit = di.Unit,
            componentPath = di.ComponentPath,
        });
        return Ok(items);
    }

    /// <summary>获取机床 Probe 报出的 Sample 通道列表。</summary>
    [HttpGet("{deviceId}/sample-channels")]
    public async Task<IActionResult> GetSampleChannels(string deviceId, CancellationToken ct)
    {
        var device = await _deviceRepo.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound($"Device {deviceId} not found");

        await using var driver = _factory.Create(device.Protocol, device.Brand, device.Model);
        if (driver is not NCLinkDriver ncLink)
            return BadRequest($"Device protocol {device.Protocol} is not NC-Link");

        var connect = await ncLink.ConnectAsync(device.ConnectionConfig, ct);
        if (!connect.Success)
            return StatusCode(502, new { error = $"Connection failed: {connect.ErrorMessage}" });

        var model = ncLink.DeviceModel;
        if (model is null)
            return StatusCode(502, new { error = "Probe response missing" });

        return Ok(model.SampleChannels);
    }
}
