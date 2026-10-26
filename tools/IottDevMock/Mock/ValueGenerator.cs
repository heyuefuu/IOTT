using System.Globalization;

namespace IottDevMock.Mock;

/// <summary>确定性取值 + 场景化的质量/延迟/故障注入。</summary>
public static class ValueGenerator
{
    public static TagValueDto Read(MockDevice device, string address, string? dataType, string scenario)
    {
        var type = string.IsNullOrWhiteSpace(dataType) ? "String" : dataType!.Trim();
        var hash = StableHash(device.Id + "|" + address);
        var (quality, error) = QualityFor(scenario, hash);

        object value = type.ToLowerInvariant() switch
        {
            "bool" => (hash & 1) == 1,
            "int8" => (sbyte)(hash % 100),
            "uint8" => (byte)(hash % 200),
            "int16" => (short)(hash % 3000),
            "uint16" => (ushort)(hash % 60000),
            "int32" or "int" => (int)(hash % 100000),
            "uint32" or "uint" => (uint)(hash % 100000),
            "int64" => (long)(hash % 1000000),
            "uint64" => ((ulong)(hash % 1000000)).ToString(CultureInfo.InvariantCulture),
            "float" or "real" => Drift(scenario, hash) + (float)((hash % 1000) / 10.0),
            "double" => Drift(scenario, hash) + (hash % 1000) / 10.0,
            "bytearray" => new[] { hash % 255, (hash >> 8) % 255, (hash >> 16) % 255, (hash >> 24) % 255 },
            _ => $"SIM-{hash % 100000:X}",
        };

        return new TagValueDto
        {
            Address = address,
            DataType = type,
            Value = value,
            Quality = quality,
            Timestamp = DateTimeOffset.UtcNow,
            ErrorMessage = error,
        };
    }

    /// <summary>DataDrift 场景下叠加随时间缓慢漂移的值。</summary>
    private static double Drift(string scenario, int hash) =>
        scenario == "DataDrift" ? Math.Sin(DateTime.UtcNow.TimeOfDay.TotalSeconds / 10.0 + hash % 6) * 5.0 : 0.0;

    private static (string Quality, string? Error) QualityFor(string scenario, int hash) => scenario switch
    {
        "PartialBad" when hash % 7 == 0 => ("Bad", "Simulated device error"),
        "PartialBad" when hash % 11 == 0 => ("Uncertain", null),
        "IntermittentFault" when hash % 9 == 0 => ("Bad", "Simulated intermittent fault"),
        _ => ("Good", null),
    };

    public static int LatencyMs(string scenario, int hash) => scenario switch
    {
        "HighLatency" => 200 + hash % 300,
        "IntermittentFault" when hash % 9 == 0 => 3000,
        _ => 5 + hash % 40,
    };

    private static int StableHash(string text)
    {
        unchecked
        {
            const int fnv = 16777619;
            var hash = (int)2166136261;
            foreach (var c in text) hash = (hash ^ c) * fnv;
            return hash & 0x7fffffff;
        }
    }
}
