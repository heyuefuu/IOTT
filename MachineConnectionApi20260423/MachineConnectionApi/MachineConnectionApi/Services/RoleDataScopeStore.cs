namespace MachineConnectionApi.Services;

using IndustrialIoT.Security;

public sealed record RoleDataScope(string Role, DeviceDataScope Scope);

public static class RoleDataScopeStore
{
    private static JsonFileStore<RoleDataScope> Store => new("role-data-scopes.json");
    public static IReadOnlyList<RoleDataScope> List() => Store.ReadAll();
    public static DeviceDataScope Read(string role) => Store.ReadAll()
        .FirstOrDefault(row => row.Role == role)?.Scope ?? DeviceDataScope.None;

    public static void Save(string role, DeviceDataScope scope)
    {
        if (role is not ("operator" or "viewer")) throw new ArgumentException("仅可设置操作员和查看者角色的数据范围。");
        DeviceDataScope.Validate(scope);
        Store.Update(rows => { rows.RemoveAll(row => row.Role == role); rows.Add(new(role, scope)); return 0; });
    }
}
