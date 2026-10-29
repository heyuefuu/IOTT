namespace MachineConnectionApi.Controllers;

using System.Text;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/system/logs")]
public sealed partial class SystemLogsController : ControllerBase
{
    private readonly ISystemActivityLog _log;

    public SystemLogsController(ISystemActivityLog log) => _log = log;

    [HttpGet]
    public ActionResult<IReadOnlyList<SystemLogDto>> List(
        [FromQuery] string? type,
        [FromQuery] string? user,
        [FromQuery] string? keyword,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] bool includeArchived = false)
    {
        if (InvalidRange(from, to)) return BadRequest(new { error = "from 不能晚于 to" });
        try { return Ok(SelectRows(type, user, keyword, from, to, includeArchived)); }
        catch (Exception error) when (IsStorageError(error)) { return StorageError(error); }
    }

    [HttpPost]
    public ActionResult<SystemLogDto> Create([FromBody] SystemLogDto input)
    {
        try { return Ok(_log.Append(input)); }
        catch (Exception error) when (IsStorageError(error)) { return StorageError(error); }
    }

    [HttpGet("export")]
    public IActionResult Export([FromQuery] string? type, [FromQuery] string? user, [FromQuery] string? keyword,
        [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null,
        [FromQuery] bool includeArchived = false)
    {
        if (InvalidRange(from, to)) return BadRequest(new { error = "from 不能晚于 to" });
        try
        {
            var rows = SelectRows(type, user, keyword, from, to, includeArchived);
            var csv = new StringBuilder("Id,Type,User,Action,Ip,Timestamp,Detail\r\n");
            foreach (var row in rows)
                csv.AppendLine(string.Join(',', [Esc(row.Id), Esc(row.Type), Esc(row.User), Esc(row.Action), Esc(row.Ip), Esc(row.Timestamp.ToString("O")), Esc(row.Detail)]));
            return File(Encoding.UTF8.GetBytes("\uFEFF" + csv), "text/csv; charset=utf-8", $"system-logs-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv");
        }
        catch (Exception error) when (IsStorageError(error)) { return StorageError(error); }
    }

    private static string Esc(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
