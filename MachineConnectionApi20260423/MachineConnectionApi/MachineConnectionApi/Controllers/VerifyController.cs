using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace MachineConnectionApi.Controllers;

[ApiController]
[Route("api/verify")]
public sealed class VerifyController : ControllerBase
{
    private readonly IVerifyAutomationService _service;
    private readonly VerifyExecutionLeaseService _leases;

    public VerifyController(IVerifyAutomationService service, VerifyExecutionLeaseService? leases = null)
    {
        _service = service;
        _leases = leases ?? VerifyExecutionLeaseService.Exclusive;
    }

    [HttpPost("run")]
    public async Task<ActionResult<VerifyRunResponse>> Run(
        [FromBody] VerifyRunRequest request,
        CancellationToken ct)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            if (string.IsNullOrWhiteSpace(request.DeviceId))
                throw new ArgumentException("请明确选择本次验证的设备；不允许默认测试全部设备。");
            // Detach the lease inputs and the engine inputs from the caller's mutable collections.
            var owned = System.Text.Json.JsonSerializer.Deserialize<VerifyRunRequest>(
                System.Text.Json.JsonSerializer.Serialize(request))!;
            owned.DeviceId = owned.DeviceId!.Trim();
            owned.ReportProgress = request.ReportProgress;
            using var lease = await _leases.AcquireAsync(owned.DeviceId, owned.Options, ct);
            return Ok(await _service.RunAsync(owned, ct));
        }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (VerifyTaskCapacityException error) { return Conflict(new { error = error.Message }); }
    }
}
