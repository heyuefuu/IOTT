namespace IndustrialIoT.Protocols.SiemensS7;

public readonly record struct S7DataLength(ushort Bytes, bool IsBit)
{
    public static readonly S7DataLength Bit = new(1, true);
    public static readonly S7DataLength Byte = new(1, false);
    public static readonly S7DataLength Word = new(2, false);
    public static readonly S7DataLength DWord = new(4, false);
    public static readonly S7DataLength LWord = new(8, false);
    public static S7DataLength BytesOf(ushort bytes) => new(bytes, false);
}

public sealed record S7Address
{
    public required S7MemoryArea Area { get; init; }
    public required ushort DbNumber { get; init; }
    public required int BitAddress { get; init; }
    public required ushort Length { get; init; }
    public required bool IsBit { get; init; }

    public int ByteOffset => BitAddress / 8;
    public int BitOffset => BitAddress % 8;

    public static S7Address Parse(string address, S7DataLength length)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new FormatException("S7 address cannot be empty.");

        var normalized = address.Trim().ToUpperInvariant();
        return normalized switch
        {
            var value when value.StartsWith("DB", StringComparison.Ordinal) => ParseDb(value, length),
            var value when value.StartsWith("SM", StringComparison.Ordinal) => ParseArea(value, S7MemoryArea.SystemMarker, 2, length),
            var value when value.StartsWith("AI", StringComparison.Ordinal) => ParseArea(value, S7MemoryArea.AnalogInput, 2, length),
            var value when value.StartsWith("AQ", StringComparison.Ordinal) => ParseArea(value, S7MemoryArea.AnalogOutput, 2, length),
            var value when value[0] == 'P' => ParseArea(value, S7MemoryArea.Peripheral, 1, length),
            var value when value[0] == 'I' => ParseArea(value, S7MemoryArea.Input, 1, length),
            var value when value[0] == 'Q' => ParseArea(value, S7MemoryArea.Output, 1, length),
            var value when value[0] == 'M' => ParseArea(value, S7MemoryArea.Marker, 1, length),
            var value when value[0] == 'V' => ParseV(value, length),
            var value when value[0] == 'T' => ParseCounterTimer(value, S7MemoryArea.Timer, length),
            var value when value[0] == 'C' => ParseCounterTimer(value, S7MemoryArea.Counter, length),
            _ => throw new FormatException($"Unsupported S7 address '{address}'."),
        };
    }

    private static S7Address ParseDb(string address, S7DataLength length)
    {
        var dot = address.IndexOf('.');
        if (dot <= 2) throw new FormatException($"Invalid S7 DB address '{address}'.");
        var dbNumber = ushort.Parse(address[2..dot]);
        var offset = address[(dot + 1)..];
        if (offset.StartsWith("DBX", StringComparison.Ordinal) ||
            offset.StartsWith("DBB", StringComparison.Ordinal) ||
            offset.StartsWith("DBW", StringComparison.Ordinal) ||
            offset.StartsWith("DBD", StringComparison.Ordinal))
        {
            offset = offset[3..];
        }

        return Create(S7MemoryArea.DataBlock, dbNumber, offset, length, isCounterTimer: false);
    }

    private static S7Address ParseArea(string address, S7MemoryArea area, int prefixLength, S7DataLength length)
    {
        var offset = address[prefixLength..];
        if (offset.Length > 0 && offset[0] is 'X' or 'B' or 'W' or 'D')
            offset = offset[1..];
        return Create(area, 0, offset, length, isCounterTimer: false);
    }

    private static S7Address ParseV(string address, S7DataLength length)
    {
        var offset = address[1..];
        if (offset.Length > 0 && offset[0] is 'X' or 'B' or 'W' or 'D')
            offset = offset[1..];
        return Create(S7MemoryArea.DataBlock, 1, offset, length, isCounterTimer: false);
    }

    private static S7Address ParseCounterTimer(string address, S7MemoryArea area, S7DataLength length) =>
        Create(area, 0, address[1..], length, isCounterTimer: true);

    private static S7Address Create(S7MemoryArea area, ushort dbNumber, string offset, S7DataLength length, bool isCounterTimer) =>
        new()
        {
            Area = area,
            DbNumber = dbNumber,
            BitAddress = ParseBitAddress(offset, isCounterTimer),
            Length = length.Bytes,
            IsBit = length.IsBit,
        };

    private static int ParseBitAddress(string offset, bool isCounterTimer)
    {
        if (string.IsNullOrWhiteSpace(offset))
            throw new FormatException("S7 address offset cannot be empty.");
        if (isCounterTimer) return int.Parse(offset);
        var parts = offset.Split('.', 2);
        var byteOffset = int.Parse(parts[0]);
        var bitOffset = parts.Length == 2 ? int.Parse(parts[1]) : 0;
        if (bitOffset is < 0 or > 7)
            throw new FormatException($"S7 bit offset must be 0-7, got {bitOffset}.");
        return byteOffset * 8 + bitOffset;
    }
}
