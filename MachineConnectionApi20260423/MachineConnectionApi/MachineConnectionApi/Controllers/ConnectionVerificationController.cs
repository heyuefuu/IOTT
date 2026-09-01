using Microsoft.AspNetCore.Mvc;
using MachineConnectionApi.Proxy;

namespace MachineConnectionApi.Controllers;

/// <summary>
/// 真机批量并行连接验证 API：转发至 Industrial IoT。
/// </summary>
[ApiController]
[Route("api/connection-verification")]
public class ConnectionVerificationController : IndustrialIoTProxyControllerBase
{
    private readonly IConfiguration _configuration;

    public ConnectionVerificationController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ConnectionVerificationController> logger)
        : base(httpClientFactory, logger)
    {
        _configuration = configuration;
    }

    private string VerificationPath =>
        _configuration["IndustrialIoT:ConnectionVerificationPath"] ?? "api/connection-verification";

    /// <summary>批量并行连接验证（deviceIds 或 deviceType，可配置并发数）</summary>
    [HttpPost("start")]
    public Task<IActionResult> Start(CancellationToken ct) =>
        ProxyForwardAsync(HttpMethod.Post, $"{VerificationPath}/start", ct);

    /// <summary>获取某次批量验证结果</summary>
    [HttpGet("{testId}")]
    public Task<IActionResult> GetResult(string testId, CancellationToken ct) =>
        ProxyForwardAsync(
            HttpMethod.Get,
            $"{VerificationPath}/{Uri.EscapeDataString(testId)}",
            ct);
}
