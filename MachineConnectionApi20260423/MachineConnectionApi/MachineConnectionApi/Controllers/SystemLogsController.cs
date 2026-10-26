namespace MachineConnectionApi.Controllers;

using System.Text;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/system/logs")]
public sealed class SystemLogsController : ControllerBase
{
    private readonly ISystemActivityLog _log;

    public SystemLogsController(ISystemActivityLog log) => _log = log;

    [HttpGet]
    public ActionResult<IReadOnlyList<SystemLogDto>> List(
        [FromQuery] string? type,
        [FromQuery] string? user,
        [FromQuery] string? device,
        [FromQuery] string? result,
        [FromQuery] string? keyword)
    {
        var query = _log.ReadAll().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(x => x.Type == type);
        if (!string.IsNullOrWhiteSpace(user)) query = query.Where(x => x.User.Contains(user, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(device)) query = query.Where(x => x.Device.Contains(device, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(result)) query = query.Where(x => string.Equals(x.Result, result, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(keyword))
            query = query.Where(x => x.Action.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.Detail.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return Ok(query.OrderByDescending(x => x.Timestamp).ToList());
    }

    /// <summary>已归档的历史日志（溢出主文件后的记录）。</summary>
    [HttpGet("archive")]
    public ActionResult<IReadOnlyList<SystemLogDto>> Archive([FromQuery] string? device, [FromQuery] int take = 500)
    {
        var query = _log.ReadArchive().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(device))
            query = query.Where(x => x.Device.Contains(device, StringComparison.OrdinalIgnoreCase));
        return Ok(query.OrderByDescending(x => x.Timestamp).Take(Math.Clamp(take, 1, 2000)).ToList());
    }

    [HttpPost]
    public ActionResult<SystemLogDto> Create([FromBody] SystemLogDto input) => Ok(_log.Append(input));

    [HttpGet("export")]
    public IActionResult Export(
        [FromQuery] string? type,
        [FromQuery] string? user,
        [FromQuery] string? device,
        [FromQuery] string? result,
        [FromQuery] string? keyword)
    {
        var resultRows = List(type, user, device, result, keyword).Value ?? [];
        var csv = new StringBuilder("Id,Type,User,Device,Result,Action,Ip,Timestamp,Detail\n");
        foreach (var row in resultRows)
            csv.AppendLine(string.Join(',', [
                Esc(row.Id), Esc(row.Type), Esc(row.User), Esc(row.Device), Esc(row.Result),
                Esc(row.Action), Esc(row.Ip), Esc(row.Timestamp.ToString("O")), Esc(row.Detail),
            ]));
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"system-logs-{DateTimeOffset.Now:yyyyMMddHHmmss}.csv");
    }

    private static string Esc(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
