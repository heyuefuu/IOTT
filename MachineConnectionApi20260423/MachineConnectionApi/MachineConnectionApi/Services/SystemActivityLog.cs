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
    SystemLogDto Append(SystemLogDto entry);
    /// <summary>写一条操作日志。type: operation | system | error | warning（与前端日志筛选一致）。</summary>
    /// <remarks>提供默认实现，便于轻量实现/测试替身只实现必要的成员。</remarks>
    void Write(string type, string action, string detail, string user = "系统", string ip = "-", string device = "", string result = "")
    {
    }
    /// <summary>已归档的历史日志（溢出主文件后按时间顺序落入档案文件）。默认空实现。</summary>
    List<SystemLogDto> ReadArchive() => [];
}

public sealed class SystemActivityLog : ISystemActivityLog
{
    // 主日志文件上限；超出部分按时间溢出到档案文件，避免 JSON 无限膨胀，同时保留历史可查
    private const int MaxEntries = 2000;
    // 档案文件上限
    private const int MaxArchiveEntries = 20000;

    private readonly JsonFileStore<SystemLogDto> _store = new("system-logs.json");
    private readonly JsonFileStore<SystemLogDto> _archive = new("system-logs-archive.json");

    public List<SystemLogDto> ReadAll() => _store.ReadAll();

    public List<SystemLogDto> ReadArchive() => _archive.ReadAll();

    public SystemLogDto Append(SystemLogDto entry)
    {
        return _store.Update(rows =>
        {
            var item = entry with
            {
                Id = string.IsNullOrWhiteSpace(entry.Id)
                    ? Guid.NewGuid().ToString("N") : entry.Id,
            };
            rows.Add(item);
            if (rows.Count > MaxEntries)
            {
                var ordered = rows.OrderBy(x => x.Timestamp).ToList();
                var overflow = ordered.Take(rows.Count - MaxEntries).ToList();
                if (overflow.Count > 0)
                {
                    _archive.Update(archive =>
                    {
                        archive.AddRange(overflow);
                        if (archive.Count > MaxArchiveEntries)
                            archive.RemoveRange(0, archive.Count - MaxArchiveEntries);
                        return 0;
                    });
                }
                rows.Clear();
                rows.AddRange(ordered.Skip(overflow.Count));
            }
            return item;
        });
    }

    public void Write(string type, string action, string detail, string user = "系统", string ip = "-", string device = "", string result = "") =>
        Append(new SystemLogDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = type,
            User = user,
            Action = action,
            Ip = ip,
            Timestamp = DateTimeOffset.Now,
            Detail = detail,
            Device = device,
            Result = result,
        });
}
