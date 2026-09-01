namespace IndustrialIoT.Protocols.SiemensS7;

using System.Buffers.Binary;
using System.Text;
using IndustrialIoT.Domain.Enums;

public static class S7ValueCodec
{
    private delegate void SpanWriter(Span<byte> span);

    public static S7DataLength GetLength(DataType dataType, ushort stringLength = 16) => dataType switch
    {
        DataType.Bool => S7DataLength.Bit,
        DataType.Int16 or DataType.UInt16 => S7DataLength.Word,
        DataType.Int32 or DataType.UInt32 or DataType.Float => S7DataLength.DWord,
        DataType.Int64 or DataType.Double => S7DataLength.LWord,
        DataType.String => S7DataLength.BytesOf(stringLength),
        DataType.ByteArray => S7DataLength.Byte,
        _ => S7DataLength.Word,
    };

    public static byte[] GetBytes(DataType dataType, object value, ushort stringLength) => dataType switch
    {
        DataType.Bool => [Convert.ToBoolean(value) ? (byte)1 : (byte)0],
        DataType.Int16 => WriteBytes(2, span => BinaryPrimitives.WriteInt16BigEndian(span, Convert.ToInt16(value))),
        DataType.UInt16 => WriteBytes(2, span => BinaryPrimitives.WriteUInt16BigEndian(span, Convert.ToUInt16(value))),
        DataType.Int32 => WriteBytes(4, span => BinaryPrimitives.WriteInt32BigEndian(span, Convert.ToInt32(value))),
        DataType.UInt32 => WriteBytes(4, span => BinaryPrimitives.WriteUInt32BigEndian(span, Convert.ToUInt32(value))),
        DataType.Float => WriteBytes(4, span => BinaryPrimitives.WriteInt32BigEndian(span, BitConverter.SingleToInt32Bits(Convert.ToSingle(value)))),
        DataType.Int64 => WriteBytes(8, span => BinaryPrimitives.WriteInt64BigEndian(span, Convert.ToInt64(value))),
        DataType.Double => WriteBytes(8, span => BinaryPrimitives.WriteInt64BigEndian(span, BitConverter.DoubleToInt64Bits(Convert.ToDouble(value)))),
        DataType.String => GetFixedStringBytes(Convert.ToString(value) ?? string.Empty, stringLength),
        DataType.ByteArray => value is byte[] bytes ? bytes : throw new InvalidCastException("ByteArray value must be byte[]."),
        _ => WriteBytes(2, span => BinaryPrimitives.WriteUInt16BigEndian(span, Convert.ToUInt16(value))),
    };

    public static object FromBytes(ReadOnlySpan<byte> data, DataType dataType) => dataType switch
    {
        DataType.Bool => data.Length > 0 && data[0] != 0,
        DataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(data),
        DataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(data),
        DataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(data),
        DataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(data),
        DataType.Float => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(data)),
        DataType.Int64 => BinaryPrimitives.ReadInt64BigEndian(data),
        DataType.Double => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(data)),
        DataType.String => Encoding.ASCII.GetString(data).TrimEnd('\0', ' '),
        DataType.ByteArray => data.ToArray(),
        _ => BinaryPrimitives.ReadUInt16BigEndian(data),
    };

    private static byte[] WriteBytes(int length, SpanWriter writer)
    {
        var bytes = new byte[length];
        writer(bytes);
        return bytes;
    }

    private static byte[] GetFixedStringBytes(string value, ushort length)
    {
        var bytes = new byte[length];
        Encoding.ASCII.GetBytes(value, bytes);
        return bytes;
    }
}
