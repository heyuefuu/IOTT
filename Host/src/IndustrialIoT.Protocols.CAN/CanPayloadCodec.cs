namespace IndustrialIoT.Protocols.CAN;

using System.Globalization;
using System.Text;
using System.Text.Json;
using IndustrialIoT.Domain.Enums;

internal static class CanPayloadCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static object Decode(byte[] payload, DataType type, bool bigEndian)
    {
        if (payload.Length > 8) throw new FormatException("Classic CAN payload exceeds eight bytes.");
        if (type == DataType.ByteArray) return payload.ToArray();
        if (type == DataType.String) return Utf8.GetString(payload);
        var size = Size(type);
        if (payload.Length != size) throw new FormatException($"{type} requires exactly {size} CAN payload bytes; received {payload.Length}.");
        var bytes = payload.ToArray();
        if (bigEndian == BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return type switch
        {
            DataType.Bool => bytes[0] is 0 or 1 ? bytes[0] == 1 : throw new FormatException("CAN Bool must be 0 or 1."),
            DataType.Int8 => unchecked((sbyte)bytes[0]), DataType.UInt8 => bytes[0],
            DataType.Int16 => BitConverter.ToInt16(bytes), DataType.UInt16 => BitConverter.ToUInt16(bytes),
            DataType.Int32 => BitConverter.ToInt32(bytes), DataType.UInt32 => BitConverter.ToUInt32(bytes),
            DataType.Int64 => BitConverter.ToInt64(bytes), DataType.UInt64 => BitConverter.ToUInt64(bytes),
            DataType.Float => Finite(BitConverter.ToSingle(bytes)), DataType.Double => Finite(BitConverter.ToDouble(bytes)),
            _ => throw new NotSupportedException($"CAN does not support {type}."),
        };
    }

    public static byte[] Encode(object value, DataType type, bool bigEndian)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is JsonElement element) value = JsonValue(element, type);
        var culture = CultureInfo.InvariantCulture;
        var bytes = type switch
        {
            DataType.ByteArray => value is byte[] raw ? raw.ToArray() : throw new ArgumentException("CAN ByteArray requires byte[]."),
            DataType.String => value is string text ? Utf8.GetBytes(text) : throw new ArgumentException("CAN String requires a string."),
            DataType.Bool => new[] { value is bool flag ? (byte)(flag ? 1 : 0) : throw new ArgumentException("CAN Bool requires true or false.") },
            DataType.Int8 => new[] { unchecked((byte)Convert.ToSByte(Integer(value), culture)) },
            DataType.UInt8 => new[] { Convert.ToByte(Integer(value), culture) },
            DataType.Int16 => BitConverter.GetBytes(Convert.ToInt16(Integer(value), culture)),
            DataType.UInt16 => BitConverter.GetBytes(Convert.ToUInt16(Integer(value), culture)),
            DataType.Int32 => BitConverter.GetBytes(Convert.ToInt32(Integer(value), culture)),
            DataType.UInt32 => BitConverter.GetBytes(Convert.ToUInt32(Integer(value), culture)),
            DataType.Int64 => BitConverter.GetBytes(Convert.ToInt64(Integer(value), culture)),
            DataType.UInt64 => BitConverter.GetBytes(Convert.ToUInt64(Integer(value), culture)),
            DataType.Float => BitConverter.GetBytes(Finite(Convert.ToSingle(value, culture))),
            DataType.Double => BitConverter.GetBytes(Finite(Convert.ToDouble(value, culture))),
            _ => throw new NotSupportedException($"CAN does not support {type}."),
        };
        if (bytes.Length > 8) throw new ArgumentException("Classic CAN payload must contain zero to eight bytes.");
        if (type is not (DataType.String or DataType.ByteArray) && bigEndian == BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return bytes;
    }

    private static object Integer(object value)
    {
        if (value is bool) throw new ArgumentException("CAN integer cannot be a boolean.");
        var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        if (number != decimal.Truncate(number)) throw new ArgumentException("CAN integer cannot contain a fractional value.");
        return number;
    }

    private static float Finite(float value) => float.IsFinite(value) ? value : throw new FormatException("CAN Float must be finite.");
    private static double Finite(double value) => double.IsFinite(value) ? value : throw new FormatException("CAN Double must be finite.");

    private static int Size(DataType type) => type switch
    {
        DataType.Bool or DataType.Int8 or DataType.UInt8 => 1,
        DataType.Int16 or DataType.UInt16 => 2,
        DataType.Int32 or DataType.UInt32 or DataType.Float => 4,
        DataType.Int64 or DataType.UInt64 or DataType.Double => 8,
        _ => throw new NotSupportedException($"CAN does not support {type}."),
    };

    private static object JsonValue(JsonElement value, DataType type) => type switch
    {
        DataType.Bool => value.GetBoolean(), DataType.String => value.GetString()!,
        DataType.ByteArray => value.ValueKind == JsonValueKind.String ? value.GetBytesFromBase64()
            : value.EnumerateArray().Select(item => item.GetByte()).ToArray(),
        DataType.Float => value.GetSingle(), DataType.Double => value.GetDouble(),
        _ => value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : value.GetString()!,
    };
}
