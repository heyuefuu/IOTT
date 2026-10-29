namespace MachineConnectionApi.Services;

public sealed partial class VerifyTaskRunner
{
    private AuthDelegation? ExecutionIdentity(string taskId, string trigger)
    {
        if (trigger != "定时")
        {
            var delegated = AuthDelegationContext.Capture(_requestContext?.HttpContext);
            if (_authorization is not null && delegated?.InternalService != true
                && (string.IsNullOrWhiteSpace(delegated?.SessionToken) || _authorization.Validate(delegated.SessionToken) is null))
                throw new UnauthorizedAccessException("后台验证任务需要有效用户身份，缺少或已撤销的身份不会提升为系统权限。");
            return delegated;
        }
        var task = _store.ReadAll().SingleOrDefault(row => row.Id == taskId);
        if (task is null) return null;
        if (string.IsNullOrWhiteSpace(task.ScheduleOwnerUserId) || _authorization is null)
            throw new UnauthorizedAccessException("定时任务缺少有效授权归属；请由有权用户登录后重新保存定时配置。");
        return _authorization.AuthorizeScheduledTask(task.ScheduleOwnerUserId, task);
    }
}
