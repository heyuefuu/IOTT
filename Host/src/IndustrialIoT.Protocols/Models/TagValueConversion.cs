namespace IndustrialIoT.Protocols.Models;

using System.Globalization;
using System.Text.Json;
using IndustrialIoT.Domain.Enums;

public static class TagValueConversion
{
    public static object ConvertScalar(object? raw, DataType dataType)
    {
        if (raw is null) throw new FormatException("Device returned no value.");
        if (raw is JsonElement element)
        {
            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new FormatException("Device returned no value.");
            if (dataType == DataType.ByteArray && element.ValueKind == JsonValueKind.Array)
                return element.EnumerateArray().Select(item => item.GetByte()).ToArray();
            raw = element.ValueKind == JsonValueKind.String ? element.GetString()! : element.ToString();
        }
        if (dataType == DataType.ByteArray)
            return raw is byte[] bytes ? bytes.ToArray() : throw new FormatException("Expected a byte array.");

        var text = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
        const NumberStyles numeric = NumberStyles.Float;
        var culture = CultureInfo.InvariantCulture;
        object value = dataType switch
        {
            DataType.String => text,
            DataType.Bool => bool.TryParse(text, out var boolean) ? boolean
                : decimal.Parse(text, numeric, culture) != 0,
            DataType.Int8 => sbyte.Parse(text, numeric, culture),
            DataType.UInt8 => byte.Parse(text, numeric, culture),
            DataType.Int16 => short.Parse(text, numeric, culture),
            DataType.UInt16 => ushort.Parse(text, numeric, culture),
            DataType.Int32 => int.Parse(text, numeric, culture),
            DataType.UInt32 => uint.Parse(text, numeric, culture),
            DataType.Int64 => long.Parse(text, numeric, culture),
            DataType.UInt64 => ulong.Parse(text, numeric, culture),
            DataType.Float => float.Parse(text, numeric, culture),
            DataType.Double => double.Parse(text, numeric, culture),
            _ => throw new NotSupportedException($"Unsupported data type {dataType}."),
        };
        if (value is float single && !float.IsFinite(single)
            || value is double number && !double.IsFinite(number))
            throw new FormatException("Device returned a non-finite number.");
        return value is ulong unsigned ? unsigned.ToString(culture) : value;
    }
}
