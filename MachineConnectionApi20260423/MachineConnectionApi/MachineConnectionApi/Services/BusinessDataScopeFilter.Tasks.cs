namespace MachineConnectionApi.Services;

using System.Text.Json;
using IndustrialIoT.Security;
using MachineConnectionApi.Models;

public sealed partial class BusinessDataScopeFilter
{
    public bool CanReadTask(VerifyTaskDto task, DeviceDataScope scope)
        => VerifyTaskScopeAuthorization.CanAccess(task, scope, devices.ReadAll());

    private bool TaskAllowed(string id, DeviceDataScope scope) =>
        tasks.ReadAll().FirstOrDefault(task => task.Id == id) is { } task && CanReadTask(task, scope);

    private bool TaskAction(string action, IDictionary<string, object?> arguments, DeviceDataScope scope)
    {
        if (action == "List") return true;
        if (arguments.TryGetValue("id", out var id) && id is string taskId && !TaskAllowed(taskId, scope)) return false;
        if (arguments.TryGetValue("taskIds", out var ids)
            && ids is IEnumerable<string> batch && !batch.All(taskId => TaskAllowed(taskId, scope))) return false;
        if (arguments.Values.OfType<VerifyTaskDto>().Any(task => !CanReadTask(task, scope))) return false;
        if (action == "RecoverResults") return false;
        return arguments.ContainsKey("id") || arguments.ContainsKey("taskIds")
            || arguments.Values.OfType<VerifyTaskDto>().Any();
    }

    public bool CanReadKnowledge(KnowledgeRecord record, DeviceDataScope scope)
    {
        if (record.DataDeviceIds is null) return false;
        if (record.DataDeviceIds.Count > 0) return record.DataDeviceIds.All(scope.Allows);
        if (!string.IsNullOrWhiteSpace(record.SyncTaskId))
        {
            var source = tasks.ReadAll().FirstOrDefault(task => task.Id == record.SyncTaskId);
            if (source is null || !CanReadTask(source, scope)) return false;
            try { return JsonSerializer.Deserialize<VerifyRunResponse>(source.LastRunJson, JsonOptions)?.RunId == record.SyncRunId; }
            catch (JsonException) { return false; }
        }
        var candidates = devices.ReadAll().Where(device => device.Id == record.MachineNo
            || new[] { "DeviceCode", "deviceCode", "Code", "code" }.Select(key =>
                device.ExtendedProperties.GetValueOrDefault(key)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                    == record.MachineNo).ToList();
        return candidates.Count == 1 && scope.Allows(candidates[0].Id);
    }

    private bool KnowledgeAction(string action, IDictionary<string, object?> arguments, DeviceDataScope scope)
    {
        if (action is "List" or "Tasks" or "Template") return true;
        if (action == "Draft") return arguments.TryGetValue("id", out var taskId)
            && taskId is string id && TaskAllowed(id, scope);
        if (action == "Import") return false; // Unbound spreadsheet rows need an administrator to assign device ownership.
        if (arguments.TryGetValue("id", out var key) && key is string recordId
            && (knowledge.Find(recordId) is not { } old || !CanReadKnowledge(old, scope))) return false;
        return arguments.Values.OfType<KnowledgeRecord>().All(record => CanReadKnowledge(record, scope)
            && (string.IsNullOrWhiteSpace(record.SyncTaskId) || TaskAllowed(record.SyncTaskId, scope)));
    }
}
