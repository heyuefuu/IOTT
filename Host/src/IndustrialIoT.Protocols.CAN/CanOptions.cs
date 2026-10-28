namespace IndustrialIoT.Protocols.CAN;

using System.Globalization;
using IndustrialIoT.Domain.ValueObjects;

internal sealed record CanOptions(string PortName, int BaudRate, int SpeedIndex, TimeSpan MaxAge)
{
    private static readonly int[] BitRates = [10000, 20000, 50000, 100000, 125000, 250000, 500000, 800000, 1000000];

    public static string? Get(DeviceConnectionConfig config, string key) => config.ExtendedProperties
        .FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    public static CanOptions Parse(DeviceConnectionConfig config)
    {
        var transport = Get(config, "CanTransport") ?? "SLCAN";
        if (!transport.Equals("SLCAN", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("CAN transport must be SLCAN.");
        var portName = Get(config, "PortName") ?? config.Host;
        if (string.IsNullOrWhiteSpace(portName) || config.Port != 0)
            throw new ArgumentException("SLCAN requires a serial PortName and Port=0.");
        var baudRate = PositiveInt(config, "BaudRate", 115200);
        var bitRate = PositiveInt(config, "CanBitRate", 500000);
        var index = Array.IndexOf(BitRates, bitRate);
        if (index < 0) throw new ArgumentException("Unsupported CAN bit rate; select a standard SLCAN rate from 10000 to 1000000.");
        foreach (var (key, expected) in new[] { ("DataBits", "8"), ("Parity", "None"), ("StopBits", "One"), ("Handshake", "None") })
            if (Get(config, key) is { } value && !value.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"SLCAN requires {key}={expected}.");
        if (config.ReadTimeout <= TimeSpan.Zero || config.ReadTimeout.TotalMilliseconds > int.MaxValue
            || config.ConnectTimeout <= TimeSpan.Zero || config.ConnectTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentException("CAN connection and read timeouts must be positive finite millisecond durations.");
        var age = PositiveInt(config, "CanFrameMaxAgeMs", Math.Max(1, (int)config.ReadTimeout.TotalMilliseconds));
        return new(portName.Trim(), baudRate, index, TimeSpan.FromMilliseconds(age));
    }

    private static int PositiveInt(DeviceConnectionConfig config, string key, int fallback)
    {
        var text = Get(config, key);
        if (text is null) return fallback;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            throw new ArgumentException($"{key} must be a positive integer.");
        return value;
    }
}
