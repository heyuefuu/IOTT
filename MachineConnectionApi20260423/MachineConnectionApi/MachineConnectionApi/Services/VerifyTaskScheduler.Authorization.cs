namespace MachineConnectionApi.Services;

public sealed partial class VerifyTaskSchedulerHostedService
{
    private void DisableUnownedSchedules()
    {
        var disabled = _store.Update(rows =>
        {
            var names = new List<string>();
            for (var index = 0; index < rows.Count; index++)
            {
                var task = rows[index];
                if (!string.Equals(task.ScheduleType, "daily", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(task.ScheduleOwnerUserId)) continue;
                rows[index] = task with
                {
                    ScheduleType = "none",
                    Detail = task.Detail + "\n旧定时任务缺少授权归属，已停用自动执行；请有权用户登录后重新保存定时配置。",
                };
                names.Add(task.Name);
            }
            return names;
        });
        if (disabled.Count == 0) return;
        var detail = "已停用缺少授权归属的旧定时任务：" + string.Join("、", disabled);
        _logger.LogWarning("{Detail}", detail);
        _activityLog?.Write("warning", "定时任务授权迁移", detail);
    }
}
