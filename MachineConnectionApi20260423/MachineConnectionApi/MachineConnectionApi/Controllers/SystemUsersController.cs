namespace MachineConnectionApi.Controllers;

using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/system/users")]
public sealed class SystemUsersController : ControllerBase
{
    private static readonly PermissionDto[] Permissions =
    [
        new("all", "全部权限"), new("device_manage", "设备管理"), new("data_read", "数据读取"),
        new("data_write", "数据写入"), new("system_manage", "系统管理"), new("log_manage", "日志管理"),
        new("permission_manage", "权限管理"), new("report_manage", "报表管理"), new("config_manage", "配置管理"),
    ];

    private readonly IUserStore _store;
    private readonly IRoleStore _roles;
    private readonly ISystemActivityLog _activityLog;
    private readonly IAuthService _auth;

    public SystemUsersController(IUserStore store, IRoleStore roles, ISystemActivityLog activityLog, IAuthService auth)
    {
        _store = store;
        _roles = roles;
        _activityLog = activityLog;
        _auth = auth;
    }

    /// <summary>生效权限 = 被赋角色的权限并集 ∪ 直接权限（多角色）。</summary>
    private IReadOnlyList<string> MergeRolePermissions(IReadOnlyList<string>? roles, IReadOnlyList<string>? permissions)
    {
        if (roles is not { Count: > 0 }) return permissions ?? [];
        var all = _roles.ReadAll();
        var set = new HashSet<string>(permissions ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var key in roles)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var role = all.FirstOrDefault(r =>
                string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.Id, key, StringComparison.OrdinalIgnoreCase));
            if (role is null) continue;
            foreach (var p in role.Permissions) set.Add(p);
        }
        return set.ToList();
    }

    // 与运行时鉴权（AuthSession.HasPermission）保持完全一致：Role=="admin"（区分大小写），或权限包含 "all"（区分大小写）。
    // 若此处判定比运行时宽松（例如把 "Admin" 也算管理员），就可能放行"删除唯一真正管理员"的操作。
    private static bool IsAdmin(AppUserDto user) =>
        string.Equals(user.Role, "admin", StringComparison.Ordinal)
        || (user.Permissions ?? []).Contains("all", StringComparer.Ordinal);

    /// <summary>可用管理员：启用状态 + 已设置口令 + 具备管理员权限。用于"至少保留一个可用管理员"的防误删/误禁保护。</summary>
    private static bool IsUsableAdmin(AppUserDto user) =>
        string.Equals(user.Status, "启用", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(user.PasswordHash)
        && IsAdmin(user);

    /// <summary>统计可用管理员数量（可排除某下标），用于原子地判断操作后是否仍至少留一个。</summary>
    private static int CountUsableAdmins(List<AppUserDto> rows, int excludeIndex = -1)
    {
        var count = 0;
        for (var i = 0; i < rows.Count; i++)
            if (i != excludeIndex && IsUsableAdmin(rows[i])) count++;
        return count;
    }

    // 凭据只存不出：所有对外返回一律抹掉 PasswordHash
    private static AppUserDto Redact(AppUserDto user) => user with { Password = "******", PasswordHash = null };

    [HttpGet]
    public ActionResult<IReadOnlyList<AppUserDto>> List() =>
        Ok(_store.ReadAll().Select(Redact).ToList());

    [HttpGet("permissions")]
    public ActionResult<IReadOnlyList<PermissionDto>> ListPermissions() => Ok(Permissions);

    [HttpPost]
    public ActionResult<AppUserDto> Create([FromBody] AppUserDto input)
    {
        if (string.IsNullOrWhiteSpace(input.Password) || input.Password == "******")
            return BadRequest(new { error = "新用户必须设置登录密码" });
        var created = _store.Update<(bool Success, AppUserDto? Item)>(rows =>
        {
            if (rows.Any(x => x.Username.Equals(
                    input.Username, StringComparison.OrdinalIgnoreCase)))
                return (false, null);
            var item = input with
            {
                Id = Guid.NewGuid().ToString("N"), Password = "******",
                PasswordHash = PasswordHasher.Hash(input.Password),
                Permissions = MergeRolePermissions(input.Roles, input.Permissions),
            };
            rows.Add(item);
            return (true, item);
        });
        if (!created.Success || created.Item is null)
            return Conflict(new { error = "Username already exists" });
        var item = created.Item;
        _activityLog.Write("operation", "创建用户", $"{item.Name}（{item.Username}，{item.Role}）");
        return Ok(Redact(item));
    }

    [HttpPut("{id}")]
    public ActionResult<AppUserDto> Update(string id, [FromBody] AppUserDto input)
    {
        var outcome = _store.Update<(bool Found, bool Saved, AppUserDto? Item, string? Error)>(rows =>
        {
            var index = rows.FindIndex(x => x.Id == id);
            if (index < 0) return (false, false, null, null);
            var old = rows[index];
            var newHash = string.IsNullOrWhiteSpace(input.Password) || input.Password == "******"
                ? old.PasswordHash
                : PasswordHasher.Hash(input.Password);
            var candidate = input with
            {
                Id = id, Password = "******", PasswordHash = newHash,
                Permissions = MergeRolePermissions(input.Roles, input.Permissions),
            };
            // 在序列化的 Update 内原子判定：若本次会把"最后一个可用管理员"降权/停用，则拒绝
            if (IsUsableAdmin(old) && !IsUsableAdmin(candidate) && CountUsableAdmins(rows, index) == 0)
                return (true, false, old, "系统必须保留至少一个可用的管理员账号，不能降权或停用最后一个管理员");
            rows[index] = candidate;
            return (true, true, candidate, null);
        });

        if (!outcome.Found) return NotFound();
        if (!outcome.Saved) return Conflict(new { error = outcome.Error });
        // 角色/状态/密码可能已变化，旧会话携带的快照不再可信，强制重新登录
        _auth.RevokeUserSessions(id);
        _activityLog.Write("operation", "更新用户", $"{outcome.Item!.Name}（{outcome.Item.Username}）");
        return Ok(Redact(outcome.Item));
    }

    [HttpPut("{id}/permissions")]
    public IActionResult UpdatePermissions(string id, [FromBody] IReadOnlyList<string>? permissions)
    {
        var outcome = _store.Update<(bool Found, bool Saved, AppUserDto? Item, string? Error)>(rows =>
        {
            var index = rows.FindIndex(x => x.Id == id);
            if (index < 0) return (false, false, null, null);
            var old = rows[index];
            var candidate = old with { Permissions = permissions ?? [] };
            if (IsUsableAdmin(old) && !IsUsableAdmin(candidate) && CountUsableAdmins(rows, index) == 0)
                return (true, false, old, "系统必须保留至少一个可用的管理员账号，不能移除最后一个管理员的全部权限");
            rows[index] = candidate;
            return (true, true, candidate, null);
        });

        if (!outcome.Found) return NotFound();
        if (!outcome.Saved) return Conflict(new { error = outcome.Error });
        // 会话中的权限清单是登录时的快照，吊销后按新权限重新登录生效
        _auth.RevokeUserSessions(id);
        _activityLog.Write("operation", "更新用户权限",
            $"{outcome.Item!.Username}：{string.Join("、", permissions ?? [])}");
        return Ok(Redact(outcome.Item));
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        var outcome = _store.Update<(bool Found, bool Deleted, AppUserDto? Target)>(rows =>
        {
            var index = rows.FindIndex(x => x.Id == id);
            if (index < 0) return (false, false, null);
            var target = rows[index];
            // 删除"最后一个可用管理员"（无论是删自己、还是他人用 permission_manage 删管理员）一律拒绝
            if (IsUsableAdmin(target) && CountUsableAdmins(rows, index) == 0)
                return (true, false, target);
            rows.RemoveAt(index);
            return (true, true, target);
        });

        if (!outcome.Found) return NotFound();
        if (!outcome.Deleted)
            return Conflict(new { error = "系统必须保留至少一个可用的管理员账号，无法删除最后一个管理员账号" });
        _auth.RevokeUserSessions(id);
        _activityLog.Write("operation", "删除用户", outcome.Target!.Username);
        return NoContent();
    }
}
