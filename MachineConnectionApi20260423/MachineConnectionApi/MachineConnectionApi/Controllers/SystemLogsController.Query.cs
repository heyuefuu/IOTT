namespace MachineConnectionApi.Controllers;

using MachineConnectionApi.Models;
using Microsoft.AspNetCore.Mvc;

public sealed partial class SystemLogsController
{
    [HttpGet("query")]
    public ActionResult<SystemLogPage> Query(
        [FromQuery] string? type = null, [FromQuery] string? user = null, [FromQuery] string? keyword = null,
        [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null,
        [FromQuery] bool includeArchived = true, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (InvalidRange(from, to)) return BadRequest(new { error = "from 不能晚于 to" });
        if (page < 1 || pageSize is < 1 or > 1000)
            return BadRequest(new { error = "page 必须 >= 1，pageSize 必须为 1~1000" });
        try
        {
            var rows = SelectRows(type, user, keyword, from, to, includeArchived);
            var offset = (long)(page - 1) * pageSize;
            var items = offset >= rows.Count ? [] : rows.Skip((int)offset).Take(pageSize).ToList();
            return Ok(new SystemLogPage(items, rows.Count, page, pageSize));
        }
        catch (Exception error) when (IsStorageError(error)) { return StorageError(error); }
    }

    [HttpGet("retention")]
    public ActionResult<SystemLogRetention> Retention() => Ok(_log.Retention);

    private List<SystemLogDto> SelectRows(string? type, string? user, string? keyword,
        DateTimeOffset? from, DateTimeOffset? to, bool includeArchived)
    {
        var rows = _log.ReadAll(includeArchived).AsEnumerable();
        if (!string.IsNullOrWhiteSpace(type)) rows = rows.Where(row => row.Type == type);
        if (!string.IsNullOrWhiteSpace(user)) rows = rows.Where(row => row.User.Contains(user, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(keyword))
            rows = rows.Where(row => row.Action.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || row.Detail.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        if (from.HasValue) rows = rows.Where(row => row.Timestamp >= from.Value);
        if (to.HasValue) rows = rows.Where(row => row.Timestamp <= to.Value);
        return rows.OrderByDescending(row => row.Timestamp).ThenBy(row => row.Id, StringComparer.Ordinal).ToList();
    }

    private static bool InvalidRange(DateTimeOffset? from, DateTimeOffset? to) => from.HasValue && to.HasValue && from.Value > to.Value;
    private static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException;
    private ObjectResult StorageError(Exception error) => StatusCode(500, new { error = error.Message });
}
