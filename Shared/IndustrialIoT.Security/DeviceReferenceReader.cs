namespace IndustrialIoT.Security;

using System.Text.Json;

public static class DeviceReferenceReader
{
    private static readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase)
        { "DeviceId", "MachineId", "ConcurrentDeviceIds", "TransferDeviceIds" };

    public static IEnumerable<string> Read(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (Keys.Contains(property.Name))
                {
                    if (property.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(property.Value.GetString())) yield return property.Value.GetString()!;
                    if (property.Value.ValueKind == JsonValueKind.Array)
                        foreach (var id in property.Value.EnumerateArray())
                            if (id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString())) yield return id.GetString()!;
                }
                else foreach (var id in Read(property.Value)) yield return id;
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray())
                foreach (var id in Read(element)) yield return id;
    }
}
