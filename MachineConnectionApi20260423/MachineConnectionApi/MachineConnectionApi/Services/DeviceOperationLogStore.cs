namespace MachineConnectionApi.Services;

using MachineConnectionApi.Models;

/// <summary>
/// 设备读写操作审计历史（App_Data/device-operation-logs.json），最新在前，最多保留 5000 条。
/// 供 PLC/机器人 读写「历史记录（原值/新值/操作人/时间）」与回滚查询。
/// </summary>
public sealed class DeviceOperationLogStore
{
    private const int MaxEntries = 5000;
    private readonly JsonFileStore<DeviceOperationLogDto> _store = new("device-operation-logs.json");

    public void Append(IEnumerable<DeviceOperationLogDto> entries)
    {
        var list = entries.ToList();
        if (list.Count == 0) return;
        _store.Update(rows =>
        {
            rows.InsertRange(0, list);
            if (rows.Count > MaxEntries)
                rows.RemoveRange(MaxEntries, rows.Count - MaxEntries);
            return 0;
        });
    }

    public IReadOnlyList<DeviceOperationLogDto> Query(string? deviceId, string? type, string? keyword, int take)
    {
        var query = _store.ReadAll().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(deviceId))
            query = query.Where(x => string.Equals(x.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(type))
            query = query.Where(x => string.Equals(x.DeviceType, type, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(keyword))
            query = query.Where(x => x.Address.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.DeviceName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.User.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return query.Take(Math.Clamp(take, 1, 2000)).ToList();
    }
}
