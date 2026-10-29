namespace MachineConnectionApi.Controllers;

using MachineConnectionApi.Proxy;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/system/logs/host")]
public sealed class HostRuntimeLogsController(IHttpClientFactory clients, ILogger<HostRuntimeLogsController> logger)
    : IndustrialIoTProxyControllerBase(clients, logger)
{
    [HttpGet]
    public Task<IActionResult> Query(CancellationToken ct) =>
        ProxyTextAsync(HttpMethod.Get, "api/runtime-logs" + Request.QueryString, null, ct);
}
