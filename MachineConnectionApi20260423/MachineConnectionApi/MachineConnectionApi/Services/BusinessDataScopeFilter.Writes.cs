namespace MachineConnectionApi.Services;

using MachineConnectionApi.Models;

public sealed partial class BusinessDataScopeFilter
{
    public bool WritePermissionAllowed(string controller, string action,
        IDictionary<string, object?> arguments, AuthSession session)
    {
        if (session.HasPermission("data_write") || session.HasPermission("device_manage")) return true;
        if (controller == "Verify")
            return !arguments.Values.OfType<VerifyRunRequest>().Any(request => request.Options?.AllowFileWrites == true);
        if (controller != "VerifyTasks") return true;
        if (action is "Create" or "Update")
            return !arguments.Values.OfType<VerifyTaskDto>().Any(task => task.Options?.AllowFileWrites == true);
        if (action is not ("Run" or "Start" or "RunBatch" or "StartBatch" or "Resume")) return true;
        var ids = arguments.Where(pair => pair.Key == "id").Select(pair => pair.Value as string)
            .OfType<string>().Concat(arguments.Values.OfType<IEnumerable<string>>().SelectMany(value => value));
        return !tasks.ReadAll().Where(task => ids.Contains(task.Id)).Any(task => task.Options?.AllowFileWrites == true);
    }
}
