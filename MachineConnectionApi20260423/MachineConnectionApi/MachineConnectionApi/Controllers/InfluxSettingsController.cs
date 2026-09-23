namespace MachineConnectionApi.Controllers;

using MachineConnectionApi.Models;
using MachineConnectionApi.Options;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

[ApiController]
[Route("api/telemetry/influx/settings")]
public sealed class InfluxSettingsController(
    InfluxSettingsStore store,
    IOptionsMonitor<InfluxDbOptions> options,
    InfluxConnectionTester tester,
    ILogger<InfluxSettingsController> logger) : ControllerBase
{
    [HttpGet]
    public ActionResult<InfluxSettingsResponse> Get() => InfluxSettingsResponse.From(options.CurrentValue);

    [HttpPut]
    public ActionResult<InfluxSettingsResponse> Put([FromBody] InfluxSettingsRequest? request)
    {
        if (request is null) return BadRequest(new { error = "请提供历史库设置。" });
        try
        {
            return InfluxSettingsResponse.From(store.Save(request, options.CurrentValue));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "历史库设置保存失败");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "历史库设置保存失败，请检查 App_Data 目录的写入权限。" });
        }
    }

    [HttpPost("test")]
    public async Task<ActionResult<InfluxConnectionTestResult>> Test(
        [FromBody] InfluxSettingsRequest? request, CancellationToken ct)
    {
        if (request is null)
            return Ok(new InfluxConnectionTestResult(false, "请提供待测试的历史库设置。"));
        try
        {
            var candidate = InfluxSettingsStore.Resolve(request, options.CurrentValue);
            return Ok(await tester.TestAsync(candidate, ct));
        }
        catch (ArgumentException ex)
        {
            return Ok(new InfluxConnectionTestResult(false, ex.Message));
        }
    }
}
