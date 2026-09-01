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
    private readonly ISystemActivityLog _activityLog;
    private readonly IAuthService _auth;

    public SystemUsersController(IUserStore store, ISystemActivityLog activityLog, IAuthService auth)
    {
        _store = store;
        _activityLog = activityLog;
        _auth = auth;
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
        var item = _store.Update<AppUserDto?>(rows =>
        {
            var index = rows.FindIndex(x => x.Id == id);
            if (index < 0) return null;
            var newHash = string.IsNullOrWhiteSpace(input.Password) || input.Password == "******"
                ? rows[index].PasswordHash
                : PasswordHasher.Hash(input.Password);
            rows[index] = input with
            {
                Id = id, Password = "******", PasswordHash = newHash,
            };
            return rows[index];
        });
        if (item is null) return NotFound();
        // 角色/状态/密码可能已变化，旧会话携带的快照不再可信，强制重新登录
        _auth.RevokeUserSessions(id);
        _activityLog.Write("operation", "更新用户", $"{item.Name}（{item.Username}）");
        return Ok(Redact(item));
    }

    [HttpPut("{id}/permissions")]
    public IActionResult UpdatePermissions(string id, [FromBody] IReadOnlyList<string> permissions)
    {
        var item = _store.Update<AppUserDto?>(rows =>
        {
            var index = rows.FindIndex(x => x.Id == id);
            if (index < 0) return null;
            rows[index] = rows[index] with { Permissions = permissions };
            return rows[index];
        });
        if (item is null) return NotFound();
        // 会话中的权限清单是登录时的快照，吊销后按新权限重新登录生效
        _auth.RevokeUserSessions(id);
        _activityLog.Write("operation", "更新用户权限",
            $"{item.Username}：{string.Join("、", permissions)}");
        return Ok(Redact(item));
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        var target = _store.Update<AppUserDto?>(rows =>
        {
            var index = rows.FindIndex(x => x.Id == id);
            if (index < 0) return null;
            var item = rows[index];
            rows.RemoveAt(index);
            return item;
        });
        if (target is null) return NotFound();
        _auth.RevokeUserSessions(id);
        _activityLog.Write("operation", "删除用户", target.Username);
        return NoContent();
    }
}
