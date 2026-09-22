using Microsoft.AspNetCore.Mvc;
using MachineConnectionApi.Proxy;

namespace MachineConnectionApi.Controllers;

/// <summary>
/// 数据读写 Web API：将请求转发至 Industrial IoT 服务。
/// </summary>
[ApiController]
[Route("api/data")]
public class DataReadWriteController : IndustrialIoTProxyControllerBase
{
    private readonly IConfiguration _configuration;

    public DataReadWriteController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<DataReadWriteController> logger)
        : base(httpClientFactory, logger)
    {
        _configuration = configuration;
    }

    private string DataPath => _configuration["IndustrialIoT:DataPath"] ?? "api/data";

    /// <summary>批量读取点位</summary>
    [HttpPost("{deviceId}/read")]
    public async Task<IActionResult> ReadTags(string deviceId, CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        var content = CreateJsonContent(body);
        var path = $"{DataPath}/{Uri.EscapeDataString(deviceId)}/read";
        return await ProxyTextAsync(HttpMethod.Post, path, content, ct);
    }

    /// <summary>批量写入点位</summary>
    [HttpPost("{deviceId}/write")]
    public async Task<IActionResult> WriteTags(string deviceId, CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        var content = CreateJsonContent(body);
        var path = $"{DataPath}/{Uri.EscapeDataString(deviceId)}/write";
        return await ProxyTextAsync(HttpMethod.Post, path, content, ct);
    }

}
