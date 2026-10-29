namespace IndustrialIoT.Protocols.FANUC;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;

/// <summary>Controller-configured SNPX word/bit mappings; offsets are never inferred from a register name.</summary>
public sealed record FanucRegisterMap(string Address, string NativeAddress, DataType DataType,
    bool Writable = false, ushort Length = 1, byte ByteOffset = 0)
{
    public static IReadOnlyDictionary<string, FanucRegisterMap> From(DeviceConnectionConfig config)
    {
        var raw = config.ExtendedProperties.GetValueOrDefault("RobotRegisterMap");
        if (string.IsNullOrWhiteSpace(raw)) return new Dictionary<string, FanucRegisterMap>(StringComparer.OrdinalIgnoreCase);
        if (raw.Length > 65536) throw new ArgumentException("RobotRegisterMap exceeds 65536 characters.");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        var entries = JsonSerializer.Deserialize<FanucRegisterMap[]>(raw, options)
            ?? throw new ArgumentException("RobotRegisterMap must be an array.");
        var result = new Dictionary<string, FanucRegisterMap>(StringComparer.OrdinalIgnoreCase);
        if (entries.Length > 1024) throw new ArgumentException("RobotRegisterMap supports at most 1024 points.");
        foreach (var entry in entries)
        {
            if (entry is null || !Regex.IsMatch(entry.Address ?? "", @"^REG:[A-Za-z0-9_.-]{1,100}$")
                || !Enum.IsDefined(entry.DataType) || entry.Length is < 1 or > 256 || entry.ByteOffset > 1)
                throw new ArgumentException("Invalid mapped FANUC register name, type, length or byte offset.");
            var match = Regex.Match(entry.NativeAddress ?? "", entry.DataType == DataType.Bool
                ? @"^(?:I|Q|M)([1-9][0-9]*)$" : @"^(?:D|AI|AQ)([1-9][0-9]*)$");
            var words = entry.DataType switch
            { DataType.Int64 or DataType.UInt64 or DataType.Double => 4,
              DataType.Int32 or DataType.UInt32 or DataType.Float => 2,
              DataType.String or DataType.ByteArray => entry.Length, _ => 1 };
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var offset)
                || (long)offset + words - 1 > ushort.MaxValue || !result.TryAdd(entry.Address!, entry))
                throw new ArgumentException("RobotRegisterMap requires unique REG names and valid 1-based SNPX addresses within 65535.");
        }
        return result;
    }
}
