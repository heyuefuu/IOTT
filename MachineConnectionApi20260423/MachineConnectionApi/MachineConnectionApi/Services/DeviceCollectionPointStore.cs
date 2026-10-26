namespace MachineConnectionApi.Services;

using MachineConnectionApi.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Reads the device's saved collection points from the gateway database (Datacollection), which is
/// the store written by the device page "点位配置" (datacollectionApi.sync). Verification used to
/// look only at Host collection profiles, so points selected in the UI never reached the adapter.
/// </summary>
public interface IDeviceCollectionPointStore
{
    IReadOnlyList<(string Address, string DataType)> FindReadableCandidates(string deviceId, int max = 20);
}

public sealed class DeviceCollectionPointStore(IServiceScopeFactory scopes) : IDeviceCollectionPointStore
{
    private static readonly HashSet<string> ReadTypes = new(StringComparer.Ordinal)
    {
        "Bool", "Int8", "UInt8", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64",
        "Float", "Double", "String", "ByteArray",
    };

    public IReadOnlyList<(string Address, string DataType)> FindReadableCandidates(string deviceId, int max = 20)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || max <= 0) return [];
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MCConfigurationDbContext>();
        var rows = db.Datacollections.AsNoTracking()
            .Where(x => x.DeviceId == deviceId)
            .OrderBy(x => x.Id)
            .Select(x => new { x.Path, x.Datatype })
            .Take(500)
            .ToList();
        var found = new List<(string Address, string DataType)>(max);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Path) || row.Path.Length > 500 || row.Path.Any(char.IsControl)) continue;
            if (!ReadTypes.Contains(row.Datatype)) continue;
            if (found.Contains((row.Path, row.Datatype))) continue;
            found.Add((row.Path, row.Datatype));
            if (found.Count >= max) break;
        }
        return found;
    }
}
