namespace MachineConnectionApi.Services;

using System.Text.Json;
using IndustrialIoT.Security;
using MachineConnectionApi.Models;

public static class VerifyTaskScopeAuthorization
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool CanAccess(VerifyTaskDto task, DeviceDataScope scope, IReadOnlyList<MachineDeviceDto> devices)
    {
        if (scope.Mode == "all") return true;
        var references = DeviceReferenceReader.Read(JsonSerializer.SerializeToElement(new
        { task.DeviceId, task.MachineId, task.Options }, JsonOptions)).ToList();
        foreach (var snapshot in new[] { task.LastRunJson, task.CurrentRunJson })
        {
            if (string.IsNullOrWhiteSpace(snapshot)) continue;
            try
            {
                var run = JsonSerializer.Deserialize<VerifyRunResponse>(snapshot, JsonOptions);
                if (run is null) return false;
                if (!string.IsNullOrWhiteSpace(run.DeviceId)) references.Add(run.DeviceId);
                if (!string.IsNullOrWhiteSpace(run.MachineSnapshot?.Id)) references.Add(run.MachineSnapshot.Id);
                references.AddRange(DeviceReferenceReader.Read(JsonSerializer.SerializeToElement(run.OptionsSnapshot, JsonOptions)));
            }
            catch (JsonException) { return false; }
        }
        if (task.Options?.AllowFileWrites == true)
            foreach (var reference in references.ToList())
            {
                var current = reference;
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (true)
                {
                    if (!visited.Add(current) || visited.Count > 100 || !scope.Allows(current)) return false;
                    var device = devices.FirstOrDefault(device => device.Id == current);
                    if (device is null || !device.ExtendedProperties.TryGetValue("transferDeviceId", out var linked)
                        || string.IsNullOrWhiteSpace(linked) || linked == current) break;
                    current = linked;
                }
            }
        return references.Count > 0 && references.All(scope.Allows);
    }
}
