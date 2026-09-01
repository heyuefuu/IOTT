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
        [FromQuery] string? keyword)
    {
        var query = _log.ReadAll().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(x => x.Type == type);
        if (!string.IsNullOrWhiteSpace(user)) query = query.Where(x => x.User.Contains(user, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(keyword))
            query = query.Where(x => x.Action.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.Detail.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return Ok(query.OrderByDescending(x => x.Timestamp).ToList());
    }

    [HttpPost]
    public ActionResult<SystemLogDto> Create([FromBody] SystemLogDto input) => Ok(_log.Append(input));

    [HttpGet("export")]
    public IActionResult Export([FromQuery] string? type, [FromQuery] string? user, [FromQuery] string? keyword)
    {
        var result = List(type, user, keyword).Value ?? [];
        var csv = new StringBuilder("Id,Type,User,Action,Ip,Timestamp,Detail\n");
        foreach (var row in result)
            csv.AppendLine(string.Join(',', [Esc(row.Id), Esc(row.Type), Esc(row.User), Esc(row.Action), Esc(row.Ip), Esc(row.Timestamp.ToString("O")), Esc(row.Detail)]));
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"system-logs-{DateTimeOffset.Now:yyyyMMddHHmmss}.csv");
    }

    private static string Esc(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
