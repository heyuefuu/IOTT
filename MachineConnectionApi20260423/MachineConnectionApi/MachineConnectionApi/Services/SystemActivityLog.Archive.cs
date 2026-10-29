namespace MachineConnectionApi.Services;

using System.Globalization;
using MachineConnectionApi.Models;

public sealed partial class SystemActivityLog
{
    private string[] ArchiveFiles()
    {
        if (!Directory.Exists(_archivePath))
        {
            if (File.Exists(_archivePath)) throw new IOException($"日志归档路径被文件占用：{_archivePath}");
            return [];
        }
        var files = Directory.GetFiles(_archivePath, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToArray();
        foreach (var path in files) _ = ArchiveDate(path);
        return files;
    }

    private DateOnly ArchiveDate(string path)
    {
        var parts = Path.GetRelativePath(_archivePath, path).Split(Path.DirectorySeparatorChar);
        if (parts.Length != 2 || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || !DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            throw new IOException($"日志归档文件名称或目录无效：{path}");
        return day;
    }

    private List<SystemLogDto> ReadArchive(string path)
    {
        var rows = ReadBatch(path);
        var day = ArchiveDate(path);
        if (rows.Count == 0) throw new IOException($"日志归档批段为空，可能已损坏：{path}");
        if (rows.Any(row => DateOnly.FromDateTime(row.Timestamp.UtcDateTime) != day))
            throw new IOException($"日志归档记录与 UTC 日期目录不符：{path}");
        return rows;
    }

    private SystemLogDto? FindArchived(string id)
    {
        foreach (var path in ArchiveFiles())
        {
            var entry = ReadArchive(path).FirstOrDefault(row => row.Id == id);
            if (entry is not null) return entry;
        }
        return null;
    }

    private void Archive(IEnumerable<SystemLogDto> entries)
    {
        foreach (var day in entries.GroupBy(row => row.Timestamp.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            foreach (var batch in day.Chunk(256))
                WriteBatch(Path.Combine(_archivePath, day.Key, Guid.NewGuid().ToString("N") + ".json"), batch, overwrite: false);
    }

    public void MaintainArchives()
    {
        lock (_gate) PruneArchives();
    }

    private void PruneArchives()
    {
        if (Retention.ArchiveRetentionDays == 0) return;
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var earliest = DateOnly.FromDayNumber((int)Math.Max(0L, (long)today.DayNumber + 1 - Retention.ArchiveRetentionDays));
        var expired = ArchiveFiles().Where(path => ArchiveDate(path) < earliest).ToArray();
        // Validate all expired segments before deleting any: corruption must remain visible.
        foreach (var path in expired) _ = ReadArchive(path);
        foreach (var path in expired) File.Delete(path);
    }
}
