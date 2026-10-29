namespace MachineConnectionApi.Services;

using MachineConnectionApi.Models;

/// <summary>
/// 系统操作日志（App_Data/system-logs.json）的唯一入口：
/// 既支撑 /api/system/logs 查询导出，也供各控制器写关键操作审计（设备增删改、任务执行、服务启停等），
/// 首页「最近活动」直接消费这些真实记录。
/// </summary>
public interface ISystemActivityLog
{
    List<SystemLogDto> ReadAll();
    List<SystemLogDto> ReadAll(bool includeArchived) => ReadAll();
    SystemLogRetention Retention => new(2000, 0);
    SystemLogDto Append(SystemLogDto entry);
    /// <summary>写一条操作日志。type: operation | system | error | warning（与前端日志筛选一致）。</summary>
    void Write(string type, string action, string detail, string user = "系统", string ip = "-");
}

public sealed partial class SystemActivityLog : ISystemActivityLog
{
    public List<SystemLogDto> ReadAll() => ReadAll(false);

    public List<SystemLogDto> ReadAll(bool includeArchived)
    {
        lock (_gate)
        {
            var rows = new Dictionary<string, SystemLogDto>(StringComparer.Ordinal);
            if (includeArchived)
                foreach (var path in ArchiveFiles())
                    foreach (var row in ReadArchive(path)) rows.TryAdd(row.Id, row);
            foreach (var row in ReadBatch(_activePath)) rows[row.Id] = row;
            return rows.Values.OrderByDescending(row => row.Timestamp).ThenBy(row => row.Id, StringComparer.Ordinal).ToList();
        }
    }

    public SystemLogDto Append(SystemLogDto entry)
    {
        lock (_gate)
        {
            ArgumentNullException.ThrowIfNull(entry);
            var rows = ReadBatch(_activePath);
            if (!string.IsNullOrWhiteSpace(entry.Id))
            {
                var existing = rows.FirstOrDefault(row => row.Id == entry.Id) ?? FindArchived(entry.Id);
                if (existing is not null) return existing;
            }
            var item = entry with
            {
                Id = string.IsNullOrWhiteSpace(entry.Id)
                    ? Guid.NewGuid().ToString("N") : entry.Id,
            };
            ValidateEntry(item, _activePath);
            rows.Add(item);
            rows = rows.DistinctBy(row => row.Id, StringComparer.Ordinal).OrderByDescending(row => row.Timestamp).ToList();
            if (rows.Count > Retention.ActiveLimit)
            {
                var count = Math.Max(rows.Count - Retention.ActiveLimit, Math.Min(256, Math.Max(1, Retention.ActiveLimit / 4)));
                var retained = rows.Count - count;
                Archive(rows.Skip(retained));
                rows = rows.Take(retained).ToList();
            }
            WriteBatch(_activePath, rows, overwrite: true);
            PruneArchives();
            return item;
        }
    }

    public void Write(string type, string action, string detail, string user = "系统", string ip = "-") =>
        Append(new SystemLogDto
        {
            Id = "",
            Type = type,
            User = user,
            Action = action,
            Ip = ip,
            Timestamp = _clock.GetUtcNow(),
            Detail = detail,
        });
}
