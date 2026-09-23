namespace IndustrialIoT.Protocols.NCLink;

using System.Globalization;
using IndustrialIoT.Protocols.Models;
using Opc.Ua;
using DataType = IndustrialIoT.Domain.Enums.DataType;
using TagQuality = IndustrialIoT.Domain.Enums.TagQuality;

internal static class OpcUaValueCodec
{
    public static DataType MapBuiltInType(BuiltInType type) => type switch
    {
        BuiltInType.Boolean => DataType.Bool,
        BuiltInType.SByte or BuiltInType.Int16 => DataType.Int16,
        BuiltInType.Byte or BuiltInType.UInt16 => DataType.UInt16,
        BuiltInType.Int32 or BuiltInType.Enumeration => DataType.Int32,
        BuiltInType.UInt32 => DataType.UInt32,
        BuiltInType.Int64 => DataType.Int64,
        BuiltInType.Float => DataType.Float,
        BuiltInType.Double => DataType.Double,
        BuiltInType.ByteString => DataType.ByteArray,
        _ => DataType.String,
    };

    public static TagValue CreateTag(string address, DataType requestedType,
        DataValue value, IServiceMessageContext context)
    {
        var quality = StatusCode.IsBad(value.StatusCode) ? TagQuality.Bad
            : StatusCode.IsUncertain(value.StatusCode) ? TagQuality.Uncertain : TagQuality.Good;
        var timestamp = value.SourceTimestamp == DateTime.MinValue ? DateTimeOffset.UtcNow
            : new DateTimeOffset(DateTime.SpecifyKind(value.SourceTimestamp, DateTimeKind.Utc));
        var result = new TagValue
        {
            Address = address, DataType = requestedType, Value = null!,
            Quality = quality, Timestamp = timestamp,
            ErrorMessage = quality == TagQuality.Good ? null : value.StatusCode.ToString(),
        };
        if (quality == TagQuality.Bad || value.Value is null) return result;
        try
        {
            var raw = value.Value;
            // UA values carry their own type. Old saved numeric metadata must not
            // force dates, arrays or structured values through numeric conversion.
            if (raw is DateTime date)
                return result with { DataType = DataType.String, Value = date.ToString("O", CultureInfo.InvariantCulture) };
            if (raw is ulong unsigned)
                return result with { DataType = DataType.String, Value = unsigned.ToString(CultureInfo.InvariantCulture) };
            if (raw is long signed && (signed > 9007199254740991L || signed < -9007199254740991L))
                return result with { DataType = DataType.String, Value = signed.ToString(CultureInfo.InvariantCulture) };
            if (raw is byte[] bytes)
                return result with { DataType = DataType.ByteArray, Value = bytes };
            if (raw is double doubleValue && !double.IsFinite(doubleValue))
                return result with { DataType = DataType.String, Value = doubleValue.ToString(CultureInfo.InvariantCulture) };
            if (raw is float floatValue && !float.IsFinite(floatValue))
                return result with { DataType = DataType.String, Value = floatValue.ToString(CultureInfo.InvariantCulture) };
            if (raw is Array or Matrix or ExtensionObject or IEncodeable or Variant or DataValue or DiagnosticInfo)
            {
                using var encoder = new JsonEncoder(context, true);
                encoder.WriteVariant("Value", value.WrappedValue);
                return result with { DataType = DataType.String, Value = encoder.CloseAndReturnText() };
            }
            if (raw is LocalizedText text)
                return result with { DataType = DataType.String, Value = text.Text ?? "" };
            if (raw is NodeId or ExpandedNodeId or QualifiedName or Uuid or Guid or System.Xml.XmlElement)
                return result with { DataType = DataType.String,
                    Value = raw is System.Xml.XmlElement xml ? xml.OuterXml : raw.ToString() ?? "" };
            if (raw is StatusCode status)
                return result with { DataType = DataType.String, Value = status.ToString() };
            return result with { Value = ConvertScalar(raw, requestedType) };
        }
        catch (Exception error)
        {
            return result with { Quality = TagQuality.Bad, Value = null!,
                ErrorMessage = $"Cannot decode {address} ({value.Value.GetType().Name}) as {requestedType}: {error.Message}" };
        }
    }

    private static object ConvertScalar(object raw, DataType target) => target switch
    {
        DataType.Bool => Convert.ToBoolean(raw, CultureInfo.InvariantCulture),
        DataType.Int16 => Convert.ToInt16(raw, CultureInfo.InvariantCulture),
        DataType.Int32 => Convert.ToInt32(raw, CultureInfo.InvariantCulture),
        DataType.Int64 => Convert.ToInt64(raw, CultureInfo.InvariantCulture),
        DataType.UInt16 => Convert.ToUInt16(raw, CultureInfo.InvariantCulture),
        DataType.UInt32 => Convert.ToUInt32(raw, CultureInfo.InvariantCulture),
        DataType.Float => Convert.ToSingle(raw, CultureInfo.InvariantCulture),
        DataType.Double => Convert.ToDouble(raw, CultureInfo.InvariantCulture),
        DataType.String => Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "",
        _ => raw,
    };
}
