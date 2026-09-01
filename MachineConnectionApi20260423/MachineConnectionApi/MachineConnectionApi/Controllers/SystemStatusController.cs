namespace MachineConnectionApi.Controllers;

using System.Diagnostics;
using MachineConnectionApi.Models;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/system/status")]
public sealed class SystemStatusController : ControllerBase
{
    private static readonly DateTimeOffset StartedAt =
        new(Process.GetCurrentProcess().StartTime);

    [HttpGet]
    public ActionResult<SystemStatusDto> Get()
    {
        var now = DateTimeOffset.Now;
        var uptime = now - StartedAt;
        return Ok(new SystemStatusDto
        {
            StartedAt = StartedAt,
            CurrentTime = now,
            Uptime = FormatUptime(uptime),
        });
    }

    private static string FormatUptime(TimeSpan value)
    {
        if (value.TotalDays >= 1)
            return $"{(int)value.TotalDays}天 {value.Hours}小时 {value.Minutes}分钟";
        if (value.TotalHours >= 1)
            return $"{value.Hours}小时 {value.Minutes}分钟";
        return $"{value.Minutes}分钟 {value.Seconds}秒";
    }
}
