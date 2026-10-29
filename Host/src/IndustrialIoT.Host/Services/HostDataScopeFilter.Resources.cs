namespace IndustrialIoT.Host.Services;

using System.Text.Json;
using IndustrialIoT.Security;

public sealed partial class HostDataScopeFilter
{
    public async Task<bool> ScopeAllowedAsync(string controller, string action,
        IDictionary<string, object?> arguments, DeviceDataScope scope, CancellationToken ct)
    {
        if (scope.Mode == "all") return true;
        if (controller is "RuntimeLogs" or "ConnectionVerification") return false;
        if ((controller, action) is ("Devices", "GetAll") or ("Collection", "GetStatus")
            or ("PlcCapabilities", "Get")) return true;
        var references = new List<string>();
        foreach (var (name, value) in arguments)
        {
            if (value is null or CancellationToken or IFormFile) continue;
            if (name.Equals("deviceId", StringComparison.OrdinalIgnoreCase) && value is string deviceId)
                references.Add(deviceId);
            else if (controller == "Devices" && name == "id" && value is string id) references.Add(id);
            else if (name is "profileId" && value is string profileId)
            {
                var profile = await profiles.GetByIdAsync(profileId, ct);
                if (profile is null) return false;
                references.Add(profile.DeviceId);
            }
            else if (name is "transferId" && value is string transferId)
            {
                var transfer = await programs.GetByIdAsync(transferId, ct);
                if (transfer is null) return false;
                references.Add(transfer.DeviceId);
            }
            else if (name is "taskId" && value is string taskId)
            {
                if (controller == "Collection")
                {
                    if (!pipeline.GetActiveTasksSnapshot().TryGetValue(taskId, out var task)) return false;
                    references.Add(task.DeviceId);
                }
                else if (controller == "ProgramTransfer")
                {
                    var task = await transfers.GetTaskAsync(taskId, ct);
                    if (task is null) return false;
                    references.Add(task.DeviceId);
                }
                else return false;
            }
            else if (value is not string)
                references.AddRange(DeviceReferenceReader.Read(JsonSerializer.SerializeToElement(value)));
        }
        if ((controller is "ProgramTransfer" or "VerificationTransfer"
            || (controller, action) == ("DataReadWrite", "ReadVerificationDirectory")) && devices is not null)
            foreach (var reference in references.ToList())
            {
                var current = reference;
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (true)
                {
                    if (!visited.Add(current) || visited.Count > 100) return false;
                    var device = await devices.GetByIdAsync(current, ct);
                    if (device is null || !scope.Allows(current)) return false;
                    if (!device.ConnectionConfig.ExtendedProperties.TryGetValue("transferDeviceId", out var linked)
                        || string.IsNullOrWhiteSpace(linked) || linked == current) break;
                    current = linked;
                }
                if (visited.Count > 100 || !scope.Allows(current)) return false;
            }
        // New registrations have no assigned scope; only all-device managers may create them.
        return controller != "Devices" || action != "Create"
            ? references.Count > 0 && references.All(scope.Allows) : false;
    }
}
