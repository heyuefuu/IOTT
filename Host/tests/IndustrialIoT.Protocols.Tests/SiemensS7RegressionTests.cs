using System.Reflection;
using System.Text.Json;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Host.Controllers;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.SiemensS7;

internal static class SiemensS7RegressionTests
{
    public static Task ScalarWidthsAsync()
    {
        var cases = new (DataType Type, object Value, byte[] Bytes)[]
        {
            (DataType.UInt8, (byte)18, [0x12]),
            (DataType.Int8, (sbyte)-128, [0x80]),
            (DataType.UInt8, byte.MaxValue, [0xFF]),
            (DataType.Int16, (short)-123, [0xFF, 0x85]),
            (DataType.UInt16, (ushort)4660, [0x12, 0x34]),
            (DataType.Int32, 5000, [0, 0, 0x13, 0x88]),
            (DataType.UInt32, uint.MaxValue, [0xFF, 0xFF, 0xFF, 0xFF]),
            (DataType.UInt64, ulong.MaxValue, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]),
        };
        foreach (var sample in cases)
        {
            TestSupport.Require(S7ValueCodec.GetLength(sample.Type).Bytes == sample.Bytes.Length, $"Wrong {sample.Type} read width");
            TestSupport.Require(Equals(S7ValueCodec.FromBytes(sample.Bytes, sample.Type), sample.Value), $"Wrong {sample.Type} decoded value");
            TestSupport.Require(S7ValueCodec.GetBytes(sample.Type, sample.Value, 16).SequenceEqual(sample.Bytes), $"Wrong {sample.Type} wire bytes");
        }
        TestSupport.Require(Equals(S7ValueCodec.FromBytes(new byte[] { 0x12, 0x34 }, DataType.UInt8), (byte)18), "Byte read included an adjacent byte");
        TestSupport.Require((int)DataType.ByteArray == 9, "Existing numeric enum values changed");
        return Task.CompletedTask;
    }

    public static Task AddressAndTransportAsync()
    {
        foreach (var address in new[] { "I0.0", "MW10", "DB1.DBW2" })
        {
            var length = S7DataLength.Word;
            TestSupport.Require(S7Address.Parse("%" + address, length) == S7Address.Parse(address, length), "TIA address prefix changed its target");
        }
        var normalize = typeof(DataReadWriteController).GetMethod("NormalizeWriteValue", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var sample in new[] { (DataType.Int8, "-128"), (DataType.Int8, "\"-128\""),
            (DataType.UInt8, "255"), (DataType.UInt8, "\"255\""), (DataType.UInt64, "\"18446744073709551615\"") })
        {
            TestSupport.Require(DataReadWriteController.TryResolveReadDataType("MB10", sample.Item1.ToString(), out var resolved, out _)
                && resolved == sample.Item1, "API rejected the imported scalar type");
            using var document = JsonDocument.Parse(sample.Item2);
            var value = normalize.Invoke(null, [sample.Item1, document.RootElement])!;
            var bytes = S7ValueCodec.GetBytes(sample.Item1, value, 16);
            TestSupport.Require(Equals(S7ValueCodec.FromBytes(bytes, sample.Item1), value), "API payload lost integer precision");
        }
        var createTag = typeof(SiemensS7Driver).GetMethod("GoodTag", BindingFlags.NonPublic | BindingFlags.Static)!;
        var tag = (TagValue)createTag.Invoke(null, ["MD0", DataType.UInt64, ulong.MaxValue])!;
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(tag));
        TestSupport.Require(payload.RootElement.GetProperty("Value").GetString() == "18446744073709551615", "UInt64 transport must preserve every digit for JavaScript clients");
        return Task.CompletedTask;
    }
}
