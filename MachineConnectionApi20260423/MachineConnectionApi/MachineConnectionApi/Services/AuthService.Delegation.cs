namespace MachineConnectionApi.Services;

using IndustrialIoT.Security;
using MachineConnectionApi.Models;

public sealed partial class AuthService
{
    public AuthDelegation AuthorizeScheduledTask(string? userId, VerifyTaskDto task)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new UnauthorizedAccessException("旧定时任务缺少授权用户，请由有权限的用户重新保存任务。");
        var user = _users.ReadAll().FirstOrDefault(user => user.Id == userId);
        if (user is null || user.Status != "启用") throw new UnauthorizedAccessException("定时任务授权用户不存在或已禁用。");
        var scope = user.Role == "admin" ? DeviceDataScope.All : user.DataScope ?? RoleDataScopeStore.Read(user.Role);
        var session = new AuthSession(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), user.Id,
            user.Username, user.Name, user.Role, user.Permissions, DateTimeOffset.UtcNow.Add(SessionLifetime))
            { DataScope = scope };
        if (!session.HasPermission("report_manage")
            || (task.Options?.AllowFileWrites == true && !session.HasPermission("data_write") && !session.HasPermission("device_manage"))
            || !VerifyTaskScopeAuthorization.CanAccess(task, scope, _devices?.ReadAll() ?? []))
            throw new UnauthorizedAccessException("定时任务授权用户的当前功能权限或设备数据范围不足。");
        _sessions[session.Token] = session;
        _activityLog.Write("operation", "定时任务授权", $"{task.Name}（{task.Id}）：按当前用户权限执行", user.Username);
        return new(session.Token);
    }
}
