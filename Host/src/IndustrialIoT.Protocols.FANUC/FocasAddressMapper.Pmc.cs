namespace IndustrialIoT.Protocols.FANUC;

using System.Buffers.Binary;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

internal sealed partial class FocasAddressMapper
{
    private static (int Length, int NativeType) GetPmcTransfer(FocasPmcAddress address, DataType dataType)
    {
        if (address.Bit.HasValue != (dataType == DataType.Bool))
            throw new ArgumentException("Bool requires an explicit PMC bit address such as R1200.0; other types require a byte address.");
        if (address.Length.HasValue && dataType != DataType.ByteArray)
            throw new ArgumentException("The length suffix is only valid for PMC ByteArray values.");
        var transfer = dataType switch
        {
            DataType.Bool or DataType.Int8 or DataType.UInt8 => (Length: 1, NativeType: 0),
            DataType.Int16 or DataType.UInt16 => (Length: 2, NativeType: 1),
            DataType.Int32 or DataType.UInt32 => (Length: 4, NativeType: 2),
            DataType.ByteArray when address.Length.HasValue => (Length: address.Length.Value, NativeType: 0),
            DataType.ByteArray => throw new ArgumentException("PMC ByteArray requires an explicit length, e.g. R1200;length=4."),
            _ => throw new ArgumentException("PMC supports Bool, 8/16/32-bit integers and ByteArray; use CNC macros for floating-point values.")
        };
        if (address.Start > ushort.MaxValue - transfer.Length + 1)
            throw new ArgumentOutOfRangeException(nameof(address), "PMC byte range exceeds 65535.");
        return transfer;
    }

    private (object Value, DataType Type) ReadPmcValue(FocasPmcAddress address, DataType dataType, BatchReadCache? cache)
    {
        var (length, nativeType) = GetPmcTransfer(address, dataType);
        var bytes = GetPmcBytes(address, length, nativeType, cache);
        // Typed FOCAS payloads are native Windows words/longs, not network-endian HSL frames.
        object value = dataType switch
        {
            DataType.Bool => (bytes[0] & (1 << address.Bit!.Value)) != 0,
            DataType.Int8 => unchecked((sbyte)bytes[0]),
            DataType.UInt8 => bytes[0],
            DataType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
            DataType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            DataType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(bytes),
            DataType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            DataType.ByteArray => bytes.ToArray(),
            _ => throw new ArgumentException("Unsupported PMC data type.")
        };
        return (value, dataType);
    }

    private byte[] GetPmcBytes(FocasPmcAddress address, int length, int nativeType, BatchReadCache? cache)
    {
        var key = (address.AreaCode, address.Start, length, nativeType);
        if (cache is not null && cache.PmcBytes.TryGetValue(key, out var cached))
            return cached;
        var bytes = new byte[length];
        var result = nativeType == 0
            ? _api.ReadPmc(_handle, address.AreaCode, address.Start, length, bytes)
            : _api.ReadPmcTyped(_handle, address.AreaCode, address.Start, length, bytes, nativeType);
        ThrowIfError(result, nativeType == 0 ? "ReadPmc" : "ReadPmcTyped");
        if (cache is not null)
            cache.PmcBytes[key] = bytes;
        return bytes;
    }

    private void WritePmcValue(FocasPmcAddress address, DataType dataType, object value)
    {
        if (address.IsLegacy)
            throw new InvalidOperationException(
                $"Legacy DI/DO/RI/RO aliases are read-only. Configure an explicit PMC address; this alias reads {address.Area}{address.Start}.{address.Bit}.");
        if (address.IsReadOnly)
            throw new InvalidOperationException($"PMC area {address.Area} is read-only.");
        var (length, nativeType) = GetPmcTransfer(address, dataType);
        byte[] bytes;
        if (dataType == DataType.Bool)
        {
            var enabled = ConvertPmcBool(value);
            bytes = GetPmcBytes(address, 1, 0, cache: null);
            var mask = (byte)(1 << address.Bit!.Value);
            bytes[0] = enabled ? (byte)(bytes[0] | mask) : (byte)(bytes[0] & ~mask);
        }
        else
        {
            bytes = EncodePmcValue(value, dataType, length);
        }
        var result = nativeType == 0
            ? _api.WritePmc(_handle, address.AreaCode, address.Start, length, bytes)
            : _api.WritePmcTyped(_handle, address.AreaCode, address.Start, length, bytes, nativeType);
        ThrowIfError(result, nativeType == 0 ? "WritePmc" : "WritePmcTyped");
    }

    private static byte[] EncodePmcValue(object value, DataType dataType, int length)
    {
        var converted = TagValueConversion.ConvertScalar(value, dataType);
        if (converted is byte[] raw)
        {
            if (raw.Length != length)
                throw new ArgumentException("PMC ByteArray value must match the address length exactly.");
            return raw;
        }
        var bytes = new byte[length];
        switch (dataType)
        {
            case DataType.Int8: bytes[0] = unchecked((byte)(sbyte)converted); break;
            case DataType.UInt8: bytes[0] = (byte)converted; break;
            case DataType.Int16: BinaryPrimitives.WriteInt16LittleEndian(bytes, (short)converted); break;
            case DataType.UInt16: BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)converted); break;
            case DataType.Int32: BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)converted); break;
            case DataType.UInt32: BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)converted); break;
            default: throw new ArgumentException("Unsupported PMC data type.");
        }
        return bytes;
    }

    private static IReadOnlyList<AddressNode> MakePmcAreaNodes() => FocasPmcAddress.Areas
        .Select(area => MakeFolder($"/CNC/PMC/{area}",
            area is 'F' or 'X' ? $"PMC {area} (read-only)" : $"PMC {area}"))
        .ToArray();

    private static IReadOnlyList<AddressNode>? GetPmcAreaChildren(string parent)
    {
        const string prefix = "/cnc/pmc/";
        if (!parent.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var name = parent[prefix.Length..].ToUpperInvariant();
        if (name.Length != 1 || !FocasPmcAddress.Areas.Contains(name[0])) return null;
        var area = name[0];
        var writable = area is not ('F' or 'X');
        var start = area == 'R' ? 1200 : area == 'D' ? 100 : 0;
        var address = $"/CNC/PMC/{area}{start}";
        return
        [
            MakeVar(address, $"{area}{start} (example)", area == 'D' ? DataType.Int16 : DataType.UInt8, true, writable),
            MakeVar($"{address}.0", $"{area}{start}.0 (example)", DataType.Bool, true, writable),
        ];
    }

    private static bool ConvertPmcBool(object value)
    {
        var text = (string)TagValueConversion.ConvertScalar(value, DataType.String);
        if (bool.TryParse(text, out var boolean)) return boolean;
        return text switch
        {
            "0" => false,
            "1" => true,
            _ => throw new ArgumentException("PMC Bool writes require true, false, 0 or 1.")
        };
    }
}
