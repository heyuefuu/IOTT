using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace MachineConnectionApi.Controllers;

/// <summary>
/// Client/Server 通讯验证 API：网关（客户端连接目标）、客户端数据源（周期探测）、服务端服务（TcpListener 监听）。
/// 全部为真实 TCP socket 操作，由 ICsConnectivityService 承载。
/// </summary>
[ApiController]
[Route("api/cs")]
public class CsController : ControllerBase
{
    private readonly ICsConnectivityService _service;
    private readonly ICsParallelReportService _reportService;

    public CsController(ICsConnectivityService service, ICsParallelReportService reportService)
    {
        _service = service;
        _reportService = reportService;
    }

    // ---------- 网关 ----------
    [HttpGet("gateways")]
    public IActionResult ListGateways() => Ok(_service.ListGateways());

    [HttpPost("gateways")]
    public IActionResult CreateGateway([FromBody] CsGateway gateway) => Ok(_service.UpsertGateway(gateway));

    [HttpPut("gateways/{id}")]
    public IActionResult UpdateGateway(string id, [FromBody] CsGateway gateway)
    {
        gateway.Id = id;
        return Ok(_service.UpsertGateway(gateway));
    }

    [HttpDelete("gateways/{id}")]
    public IActionResult DeleteGateway(string id) =>
        _service.DeleteGateway(id) ? NoContent() : NotFound();

    /// <summary>启动网关 = 真实探测目标连通性，回写运行状态与心跳。</summary>
    [HttpPost("gateways/{id}/probe")]
    public async Task<IActionResult> ProbeGateway(string id, CancellationToken ct) =>
        Ok(await _service.ProbeGatewayAsync(id, ct));

    // ---------- 客户端数据源 ----------
    [HttpGet("datasources")]
    public IActionResult ListDataSources() => Ok(_service.ListDataSources());

    [HttpPost("datasources")]
    public IActionResult CreateDataSource([FromBody] CsDataSource ds) => Ok(_service.UpsertDataSource(ds));

    [HttpPut("datasources/{id}")]
    public IActionResult UpdateDataSource(string id, [FromBody] CsDataSource ds)
    {
        ds.Id = id;
        return Ok(_service.UpsertDataSource(ds));
    }

    [HttpDelete("datasources/{id}")]
    public IActionResult DeleteDataSource(string id) =>
        _service.DeleteDataSource(id) ? NoContent() : NotFound();

    /// <summary>启用数据源 = 启动后台周期探测循环。</summary>
    [HttpPost("datasources/{id}/enable")]
    public IActionResult EnableDataSource(string id) =>
        _service.EnableDataSource(id) ? Ok(new { status = "启用" }) : NotFound();

    [HttpPost("datasources/{id}/disable")]
    public IActionResult DisableDataSource(string id)
    {
        _service.DisableDataSource(id);
        return Ok(new { status = "禁用" });
    }

    // ---------- 服务端服务 ----------
    [HttpGet("servers")]
    public IActionResult ListServers() => Ok(_service.ListServers());

    [HttpPost("servers")]
    public IActionResult CreateServer([FromBody] CsServerService svc)
    {
        try { return Ok(_service.UpsertServer(svc)); }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPut("servers/{id}")]
    public IActionResult UpdateServer(string id, [FromBody] CsServerService svc)
    {
        svc.Id = id;
        try { return Ok(_service.UpsertServer(svc)); }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpDelete("servers/{id}")]
    public IActionResult DeleteServer(string id) =>
        _service.DeleteServer(id) ? NoContent() : NotFound();

    /// <summary>启动服务端 = 真实 TcpListener 监听端口。端口占用等失败返回 409。</summary>
    [HttpPost("servers/{id}/start")]
    public async Task<IActionResult> StartServer(string id)
    {
        try
        {
            var ok = await _service.StartServerAsync(id);
            return ok ? Ok(new { status = "运行中" }) : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status409Conflict, new { error = ex.Message });
        }
    }

    [HttpPost("servers/{id}/stop")]
    public IActionResult StopServer(string id)
    {
        _service.StopServer(id);
        return Ok(new { status = "停止" });
    }

    /// <summary>查看当前连入该服务端的客户端连接列表。</summary>
    [HttpGet("servers/{id}/connections")]
    public IActionResult GetServerConnections(string id) => Ok(_service.GetServerConnections(id));

    // ---------- 并发连接压测 ----------
    /// <summary>对自起始 IP 递增的 N 个目标发起真实并发 TCP 连接，返回成功率/RTT/失败明细。</summary>
    [HttpPost("parallel-test")]
    public async Task<IActionResult> ParallelTest(
        [FromBody] CsParallelTestRequest request, CancellationToken ct)
    {
        try { return Ok(await _service.RunParallelTestAsync(request, ct)); }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>导出并发连接压测报告，format 支持 pdf/html。</summary>
    [HttpPost("parallel-test/report/{format}")]
    public IActionResult ParallelTestReport(string format, [FromBody] CsParallelReportRequest request)
    {
        try
        {
            var report = _reportService.Generate(request, format);
            return File(report.Content, report.ContentType, report.FileName);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
