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
    void Write(string type, string action, string detail, string user = "系统", string ip = "-");
}

public sealed class SystemActivityLog : ISystemActivityLog
{
    // 日志文件上限，超出后丢弃最旧记录，避免 JSON 文件无限膨胀
    private const int MaxEntries = 2000;

    private readonly JsonFileStore<SystemLogDto> _store = new("system-logs.json");

    public List<SystemLogDto> ReadAll() => _store.ReadAll();

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
                var retained = rows.OrderByDescending(x => x.Timestamp)
                    .Take(MaxEntries).ToList();
                rows.Clear();
                rows.AddRange(retained);
            }
            return item;
        });
    }

    public void Write(string type, string action, string detail, string user = "系统", string ip = "-") =>
        Append(new SystemLogDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = type,
            User = user,
            Action = action,
            Ip = ip,
            Timestamp = DateTimeOffset.Now,
            Detail = detail,
        });
}
