using Microsoft.AspNetCore.Mvc;
using MachineConnectionApi.Proxy;

namespace MachineConnectionApi.Controllers;

/// <summary>PLC 品牌/协议能力矩阵：转发上游静态清单，供前端选型与必填扩展属性提示。</summary>
[ApiController]
[Route("api/plc")]
public class PlcCapabilitiesController : IndustrialIoTProxyControllerBase
{
    private readonly IConfiguration _configuration;

    public PlcCapabilitiesController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<PlcCapabilitiesController> logger)
        : base(httpClientFactory, logger)
    {
        _configuration = configuration;
    }

    private string CapabilitiesPath =>
        _configuration["IndustrialIoT:PlcCapabilitiesPath"] ?? "api/plc/capabilities";

    [HttpGet("capabilities")]
    public Task<IActionResult> Capabilities(CancellationToken ct) =>
        ProxyForwardAsync(HttpMethod.Get, CapabilitiesPath, ct);
}

/// <summary>NC-Link 诊断：转发机床 Probe 自报模型/数据项/采样通道，排查选点问题用。</summary>
[ApiController]
[Route("api/nclink")]
public class NCLinkDiagnosticsController : IndustrialIoTProxyControllerBase
{
    private readonly IConfiguration _configuration;

    public NCLinkDiagnosticsController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<NCLinkDiagnosticsController> logger)
        : base(httpClientFactory, logger)
    {
        _configuration = configuration;
    }

    private string BasePath => _configuration["IndustrialIoT:NCLinkPath"] ?? "api/nclink";

    [HttpGet("{deviceId}/probe")]
    public Task<IActionResult> Probe(string deviceId, CancellationToken ct) =>
        ProxyForwardAsync(HttpMethod.Get, $"{BasePath}/{Uri.EscapeDataString(deviceId)}/probe", ct);

    [HttpGet("{deviceId}/dataitems")]
    public Task<IActionResult> DataItems(string deviceId, CancellationToken ct) =>
        ProxyForwardAsync(HttpMethod.Get, $"{BasePath}/{Uri.EscapeDataString(deviceId)}/dataitems", ct);

    [HttpGet("{deviceId}/sample-channels")]
    public Task<IActionResult> SampleChannels(string deviceId, CancellationToken ct) =>
        ProxyForwardAsync(HttpMethod.Get, $"{BasePath}/{Uri.EscapeDataString(deviceId)}/sample-channels", ct);
}
