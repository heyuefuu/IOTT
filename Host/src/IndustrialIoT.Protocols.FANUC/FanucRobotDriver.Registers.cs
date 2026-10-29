namespace IndustrialIoT.Protocols.FANUC;

using System.Globalization;
using System.Text;
using HslCommunication;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

public sealed partial class FanucRobotDriver
{
    private IReadOnlyDictionary<string, FanucRegisterMap> _registers = new Dictionary<string, FanucRegisterMap>();

    private async Task<TagValue> ReadRegisterAsync(string address, DataType type, FanucRegisterMap register)
    {
        if (register.DataType != type) return BadTag(address, type, $"Mapped register requires {register.DataType}.");
        var native = register.NativeAddress;
        var client = _client!;
        return type switch
        {
            DataType.Bool => RegisterResult(address, type, await client.ReadBoolAsync(native)),
            DataType.Int16 => RegisterResult(address, type, await client.ReadInt16Async(native)),
            DataType.UInt16 => RegisterResult(address, type, await client.ReadUInt16Async(native)),
            DataType.Int32 => RegisterResult(address, type, await client.ReadInt32Async(native)),
            DataType.UInt32 => RegisterResult(address, type, await client.ReadUInt32Async(native)),
            DataType.Int64 => RegisterResult(address, type, await client.ReadInt64Async(native)),
            DataType.UInt64 => RegisterResult(address, type, await client.ReadUInt64Async(native)),
            DataType.Float => RegisterResult(address, type, await client.ReadFloatAsync(native)),
            DataType.Double => RegisterResult(address, type, await client.ReadDoubleAsync(native)),
            DataType.Int8 or DataType.UInt8 => RegisterByte(address, type, register.ByteOffset, await client.ReadUInt16Async(native)),
            DataType.String or DataType.ByteArray => RegisterBuffer(address, type, await client.ReadAsync(native, register.Length)),
            _ => BadTag(address, type, "Unsupported mapped FANUC register type."),
        };
    }

    private TagValue RegisterResult<T>(string address, DataType type, OperateResult<T> result) => result.IsSuccess
        ? Ok(address, type, result.Content is ulong number ? number.ToString(CultureInfo.InvariantCulture) : (object)result.Content!)
        : BadTag(address, type, result.Message);

    private TagValue RegisterByte(string address, DataType type, byte offset, OperateResult<ushort> result)
    {
        if (!result.IsSuccess) return BadTag(address, type, result.Message);
        var number = (byte)(result.Content >> (offset * 8));
        return Ok(address, type, type == DataType.Int8 ? (object)unchecked((sbyte)number) : number);
    }

    private TagValue RegisterBuffer(string address, DataType type, OperateResult<byte[]> result) => result.IsSuccess
        ? Ok(address, type, type == DataType.String ? Encoding.UTF8.GetString(result.Content).TrimEnd('\0') : result.Content)
        : BadTag(address, type, result.Message);

    private async Task<WriteResult> WriteRegisterAsync(DataType type, object value, FanucRegisterMap register, CancellationToken ct)
    {
        if (!register.Writable) return new() { Success = false, ErrorMessage = "Mapped register is read-only." };
        if (register.DataType != type) return new() { Success = false, ErrorMessage = $"Mapped register requires {register.DataType}." };
        var native = register.NativeAddress;
        var client = _client!;
        var scalar = TagValueConversion.ConvertScalar(value, type);
        ct.ThrowIfCancellationRequested();
        OperateResult result = type switch
        {
            DataType.Bool => await client.WriteAsync(native, (bool)scalar),
            DataType.Int16 => await client.WriteAsync(native, (short)scalar),
            DataType.UInt16 => await client.WriteAsync(native, (ushort)scalar),
            DataType.Int32 => await client.WriteAsync(native, (int)scalar),
            DataType.UInt32 => await client.WriteAsync(native, (uint)scalar),
            DataType.Int64 => await client.WriteAsync(native, (long)scalar),
            DataType.UInt64 => await client.WriteAsync(native, Convert.ToUInt64(scalar, CultureInfo.InvariantCulture)),
            DataType.Float => await client.WriteAsync(native, (float)scalar),
            DataType.Double => await client.WriteAsync(native, (double)scalar),
            DataType.Int8 or DataType.UInt8 => await WriteRegisterByteAsync(register, scalar, ct),
            DataType.String or DataType.ByteArray => await client.WriteAsync(native, RegisterPayload(register, scalar)),
            _ => throw new ArgumentException("Unsupported mapped FANUC register type."),
        };
        return new() { Success = result.IsSuccess, ErrorMessage = result.IsSuccess ? null : result.Message };
    }

    private async Task<OperateResult> WriteRegisterByteAsync(FanucRegisterMap register, object value, CancellationToken ct)
    {
        var previous = await _client!.ReadUInt16Async(register.NativeAddress);
        if (!previous.IsSuccess) return previous;
        ct.ThrowIfCancellationRequested();
        var number = value is sbyte signed ? unchecked((byte)signed) : (byte)value;
        var shift = register.ByteOffset * 8;
        return await _client.WriteAsync(register.NativeAddress, (ushort)((previous.Content & ~(255 << shift)) | number << shift));
    }

    private static byte[] RegisterPayload(FanucRegisterMap register, object value)
    {
        var bytes = register.DataType == DataType.String
            ? Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "") : (byte[])value;
        if (bytes.Length > register.Length * 2) throw new ArgumentException("Value exceeds the configured mapped register buffer.");
        var padded = new byte[register.Length * 2];
        bytes.CopyTo(padded, 0);
        return padded;
    }

    private AddressNode RegisterNode(FanucRegisterMap register) => new()
    {
        Path = register.Address, DisplayName = register.Address, NodeType = AddressNodeType.Variable,
        DataType = register.DataType, IsReadable = true, IsWritable = register.Writable,
        Source = "configured-snpx-map", Description = $"SNPX {register.NativeAddress}; verify controller assignment before use.",
    };

    private async Task<Stream> ExportRegisterCatalogJsonAsync(CancellationToken ct)
    {
        var nodes = new List<AddressNode>();
        foreach (var root in await BrowseAsync(null, ct))
        {
            if (root.NodeType == AddressNodeType.Variable) { nodes.Add(root); continue; }
            foreach (var child in await BrowseAsync(root.Path, ct))
            {
                if (child.NodeType == AddressNodeType.Variable) nodes.Add(child);
                else nodes.AddRange(await BrowseAsync(child.Path, ct));
            }
        }
        return new MemoryStream(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(nodes));
    }
}
