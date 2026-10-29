namespace IndustrialIoT.Host.Controllers;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/runtime-logs")]
public sealed partial class RuntimeLogsController : ControllerBase
{
    [HttpGet]
    public IActionResult Query([FromQuery] string? keyword = null, [FromQuery] string? level = null,
        [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 100)
    {
        if (page < 1 || pageSize is < 1 or > 1000 || from > to)
            return BadRequest(new { error = "Invalid date range or pagination" });
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "logs");
            var files = Directory.Exists(directory) ? Directory.GetFiles(directory, "industrial-iot-*.log") : [];
            var records = Read(files).Where(row => (!from.HasValue || row.Timestamp >= from) &&
                (!to.HasValue || row.Timestamp <= to) &&
                (string.IsNullOrWhiteSpace(level) || row.Level.Equals(level, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(keyword) || row.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
            var skip = (long)(page - 1) * pageSize;
            var items = new List<RuntimeLogRecord>();
            long total = 0;
            foreach (var record in records)
            {
                if (total >= skip && items.Count < pageSize) items.Add(record);
                total++;
            }
            return Ok(new { items, total, page, pageSize, retainedFiles = files.Length, source = "Host", order = "file-desc,line-asc" });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return StatusCode(500, new { error = "Host log read failed: " + error.Message }); }
    }

    private static IEnumerable<RuntimeLogRecord> Read(string[] files)
    {
        foreach (var file in files.OrderDescending(StringComparer.Ordinal))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            RuntimeLogRecord? record = null;
            string? line;
            long lineNumber = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                lineNumber++;
                var match = Header().Match(line);
                if (match.Success && DateTimeOffset.TryParseExact(match.Groups[1].Value,
                    "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                {
                    if (record is not null) yield return record;
                    record = new(Path.GetFileName(file) + ":" + lineNumber, timestamp, match.Groups[2].Value, match.Groups[3].Value);
                }
                else if (record is not null && record.Message.Length < 65536)
                    record = record with { Message = record.Message + "\n" + line };
            }
            if (record is not null) yield return record;
        }
    }

    [GeneratedRegex(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} [+-]\d\d:\d\d) \[([A-Z]{3})\] (.*)$")]
    private static partial Regex Header();
    public sealed record RuntimeLogRecord(string Id, DateTimeOffset Timestamp, string Level, string Message);
}
