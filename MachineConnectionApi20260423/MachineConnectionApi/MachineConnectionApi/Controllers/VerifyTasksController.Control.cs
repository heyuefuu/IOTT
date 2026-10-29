namespace MachineConnectionApi.Controllers;

using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

public sealed partial class VerifyTasksController
{
    [HttpPost("{id}/pause")]
    public Task<IActionResult> Pause(string id, [FromQuery] string runId, CancellationToken ct) => Control(id, runId, "pause", ct);

    [HttpPost("{id}/resume")]
    public Task<IActionResult> Resume(string id, [FromQuery] string runId, CancellationToken ct) => Control(id, runId, "resume", ct);

    [HttpPost("{id}/stop")]
    public Task<IActionResult> Stop(string id, [FromQuery] string runId, CancellationToken ct) => Control(id, runId, "stop", ct);

    private async Task<IActionResult> Control(string id, string runId, string action, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(runId)) return BadRequest(new { error = "请提供当前运行标识 runId" });
        try
        {
            var result = await _runner.ControlAsync(id, runId, action, ct);
            if (result is null) return NotFound();
            return result.Applied ? Ok(result) : Accepted(result);
        }
        catch (InvalidOperationException error) { return Conflict(new { error = error.Message }); }
        catch (IOException error) { return StatusCode(503, new { error = "控制状态写入失败：" + error.Message }); }
    }
}
