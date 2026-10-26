namespace MachineConnectionApi.Services;

using MachineConnectionApi.Models;

/// <summary>角色库（App_Data/roles.json）的唯一入口：角色→权限映射与预置角色。</summary>
public interface IRoleStore
{
    List<RoleDto> ReadAll();
    TResult Update<TResult>(Func<List<RoleDto>, TResult> update);
}

public sealed class RoleStore : IRoleStore
{
    private readonly JsonFileStore<RoleDto> _store = new("roles.json");

    public RoleStore() => EnsureSeeded();

    public List<RoleDto> ReadAll() => _store.ReadAll();

    public TResult Update<TResult>(Func<List<RoleDto>, TResult> update) => _store.Update(update);

    /// <summary>首次运行预置四类角色（系统管理员/工艺工程师/设备操作员/只读观察员）。</summary>
    private void EnsureSeeded()
    {
        _store.Update(rows =>
        {
            if (rows.Count > 0) return 0;
            var now = DateTimeOffset.Now;
            rows.Add(new RoleDto
            {
                Id = "role-admin", Key = "系统管理员", Name = "系统管理员",
                Description = "全部权限", Permissions = ["all"], BuiltIn = true, CreatedAt = now,
            });
            rows.Add(new RoleDto
            {
                Id = "role-engineer", Key = "工艺工程师", Name = "工艺工程师",
                Description = "设备/配置/报表/读写",
                Permissions = ["device_manage", "data_read", "data_write", "config_manage", "report_manage"],
                BuiltIn = true, CreatedAt = now,
            });
            rows.Add(new RoleDto
            {
                Id = "role-operator", Key = "设备操作员", Name = "设备操作员",
                Description = "设备操作与读写",
                Permissions = ["device_manage", "data_read", "data_write"],
                BuiltIn = true, CreatedAt = now,
            });
            rows.Add(new RoleDto
            {
                Id = "role-viewer", Key = "只读观察员", Name = "只读观察员",
                Description = "只读与报表",
                Permissions = ["data_read", "report_manage"],
                BuiltIn = true, CreatedAt = now,
            });
            return 0;
        });
    }
}
