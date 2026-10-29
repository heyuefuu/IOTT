namespace IndustrialIoT.Host.Controllers;

using IndustrialIoT.Infrastructure.Messaging;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/local-history")]
public sealed class LocalHistoryController(LocalHistoryDataOutput history) : ControllerBase
{
    [HttpGet("retention")]
    public ActionResult<LocalHistoryPolicy> Retention() => Ok(history.Policy);

    [HttpGet("{deviceId}")]
    public async Task<IActionResult> Query(string deviceId, [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null, [FromQuery] int limit = 1000, CancellationToken ct = default)
    {
        try { return Ok(await history.QueryAsync(deviceId, from, to, limit, ct)); }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (InvalidOperationException error) { return Conflict(new { error = error.Message }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return StatusCode(500, new { error = "Local history query failed: " + error.Message }); }
    }
}
