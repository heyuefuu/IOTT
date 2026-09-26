namespace IndustrialIoT.Protocols.Modbus;

using System.IO.Ports;
using System.Text;
using HslCommunication;
using HslCommunication.Core;
using HslCommunication.ModBus;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;
using static IndustrialIoT.Protocols.Modbus.ModbusAddressParser;

[ProtocolDriver(ProtocolType.ModbusRTU, "ModbusRTU", "Modbus RTU", "PLC")]
public sealed class ModbusRtuDriver : IProtocolDriver
{
    private const ushort DefaultStringLength = 16;
    private readonly ILogger<ModbusRtuDriver> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private ModbusRtu? _client;
    private ConnectionState _state = ConnectionState.Disconnected;

    public ModbusRtuDriver(ILogger<ModbusRtuDriver> logger) => _logger = logger;
    public ProtocolType Protocol => ProtocolType.ModbusRTU;
    public ConnectionState State => _state;
    public DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.Write | DriverCapabilities.BatchRead;
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        if (_state == ConnectionState.Connected) return new() { Success = true };
        SetState(ConnectionState.Connecting);
        try
        {
            var station = byte.TryParse(Get(config, "Station") ?? Get(config, "UnitId"), out var s) ? s : (byte)1;
            var client = new ModbusRtu(station);
            ApplyOptions(client, config);
            var portName = Get(config, "PortName") ?? throw new InvalidOperationException("ModbusRTU requires ExtendedProperties['PortName'].");
            var baud = int.TryParse(Get(config, "BaudRate"), out var b) ? b : 9600;
            var dataBits = int.TryParse(Get(config, "DataBits"), out var d) ? d : 8;
            var stopBits = Enum.TryParse<StopBits>(Get(config, "StopBits"), true, out var sb) ? sb : StopBits.One;
            var parity = Enum.TryParse<Parity>(Get(config, "Parity"), true, out var p) ? p : Parity.None;
            client.SerialPortInni(sp => { sp.PortName = portName; sp.BaudRate = baud; sp.DataBits = dataBits; sp.StopBits = stopBits; sp.Parity = parity; });
            var open = await Task.Run(() => client.Open(), ct);
            if (!open.IsSuccess) throw new InvalidOperationException(open.Message);
            CleanupClient();
            _client = client;
            _logger.LogInformation("ModbusRTU connected on {Port} @ {Baud} station {Station}", portName, baud, station);
            SetState(ConnectionState.Connected);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            CleanupClient();
            _logger.LogError(ex, "ModbusRTU connection failed");
            SetState(ConnectionState.Faulted, ex.Message);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        try { _client?.Close(); } catch { }
        CleanupClient();
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_state != ConnectionState.Connected || _client is null) return false;
        await _semaphore.WaitAsync(ct);
        try { return (await _client.ReadUInt16Async("0")).IsSuccess; }
        catch { return false; }
        finally { _semaphore.Release(); }
    }

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try { return await ReadParsed(address, Parse(address, dataType), dataType); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "ModbusRTU read failed at {Address}", address); return BadTag(address, dataType, ex.Message); }
        finally { _semaphore.Release(); }
    }

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        var results = new List<TagValue>(requests.Count);
        foreach (var req in requests) results.Add(await ReadTagAsync(req.Address, req.DataType, ct));
        return results;
    }

    public async Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            var parsed = Parse(address, dataType);
            ValidateScalarWrite(parsed, dataType);
            var result = await WriteParsed(parsed, dataType, value);
            return result.IsSuccess ? new() { Success = true } : new() { Success = false, ErrorMessage = result.Message };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "ModbusRTU write failed at {Address}", address); return new() { Success = false, ErrorMessage = ex.Message }; }
        finally { _semaphore.Release(); }
    }

    public async ValueTask DisposeAsync() { await DisconnectAsync(); _semaphore.Dispose(); GC.SuppressFinalize(this); }

    private async Task<TagValue> ReadParsed(string original, ParsedAddress parsed, DataType dataType)
    {
        var c = _client!;
        var addr = Address(parsed);
        if (parsed.CountExplicit) return await ReadRangeAsync(original, parsed, dataType);
        if (parsed.RegisterType == ModbusRegisterType.Coil) return ToTagValue(original, dataType, await c.ReadCoilAsync(addr));
        if (parsed.RegisterType == ModbusRegisterType.DiscreteInput) return ToTagValue(original, dataType, await c.ReadDiscreteAsync(addr));
        return dataType switch
        {
            DataType.Bool => ToTagValue(original, dataType, await c.ReadUInt16Async(addr), v => v > 0), DataType.Int16 => ToTagValue(original, dataType, await c.ReadInt16Async(addr)), DataType.UInt16 => ToTagValue(original, dataType, await c.ReadUInt16Async(addr)),
            DataType.Int32 => ToTagValue(original, dataType, await c.ReadInt32Async(addr)), DataType.UInt32 => ToTagValue(original, dataType, await c.ReadUInt32Async(addr)), DataType.Float => ToTagValue(original, dataType, await c.ReadFloatAsync(addr)),
            DataType.Int64 => ToTagValue(original, dataType, await c.ReadInt64Async(addr)), DataType.Double => ToTagValue(original, dataType, await c.ReadDoubleAsync(addr)), DataType.String => ToTagValue(original, dataType, await c.ReadStringAsync(addr, DefaultStringLength, Encoding.ASCII)),
            DataType.UInt64 => ToTagValue(original, dataType, await c.ReadUInt64Async(addr)),
            DataType.ByteArray => ToTagValue(original, dataType, await c.ReadAsync(addr, 1)),
            _ => throw new NotSupportedException($"Unsupported Modbus data type {dataType}."),
        };
    }

    private Task<OperateResult> WriteParsed(ParsedAddress parsed, DataType dataType, object value)
    {
        var c = _client!;
        var addr = Address(parsed);
        if (parsed.RegisterType is ModbusRegisterType.InputRegister or ModbusRegisterType.DiscreteInput) throw new InvalidOperationException($"{parsed.RegisterType} is read-only.");
        return dataType switch
        {
            DataType.Bool when parsed.RegisterType == ModbusRegisterType.Coil => c.WriteAsync(addr, Convert.ToBoolean(value)),
            DataType.Bool => c.WriteAsync(addr, Convert.ToUInt16(Convert.ToBoolean(value) ? 1 : 0)),
            DataType.Int16 => c.WriteAsync(addr, Convert.ToInt16(value)), DataType.UInt16 => c.WriteAsync(addr, Convert.ToUInt16(value)),
            DataType.Int32 => c.WriteAsync(addr, new[] { Convert.ToInt32(value) }), DataType.UInt32 => c.WriteAsync(addr, new[] { Convert.ToUInt32(value) }),
            DataType.Float => c.WriteAsync(addr, new[] { Convert.ToSingle(value) }), DataType.Int64 => c.WriteAsync(addr, new[] { Convert.ToInt64(value) }),
            DataType.UInt64 => c.WriteAsync(addr, new[] { Convert.ToUInt64(value) }), DataType.Double => c.WriteAsync(addr, new[] { Convert.ToDouble(value) }),
            DataType.String => c.WriteAsync(addr, Convert.ToString(value) ?? string.Empty, DefaultStringLength, Encoding.ASCII),
            DataType.ByteArray => c.WriteAsync(addr, (byte[])value),
            _ => throw new NotSupportedException($"Unsupported Modbus data type {dataType}."),
        };
    }

    private async Task<TagValue> ReadRangeAsync(string original, ParsedAddress parsed, DataType dataType)
    {
        var client = _client!;
        var address = Address(parsed);
        var registers = checked((ushort)parsed.RegisterCount);
        if (IsBitType(parsed.RegisterType))
        {
            if (dataType != DataType.Bool) throw new FormatException("Bit ranges require Bool data type.");
            return parsed.RegisterType == ModbusRegisterType.Coil
                ? ToTagValue(original, dataType, await client.ReadCoilAsync(address, registers))
                : ToTagValue(original, dataType, await client.ReadDiscreteAsync(address, registers));
        }
        if (dataType == DataType.ByteArray)
            return ToTagValue(original, dataType, await client.ReadAsync(address, registers));
        if (dataType == DataType.String)
            return ToTagValue(original, dataType, await client.ReadAsync(address, registers),
                bytes => client.ByteTransform.TransString(bytes, 0, bytes.Length, Encoding.ASCII).TrimEnd('\0'));
        var width = GetRegisterCount(parsed.RegisterType, dataType);
        if (registers % width != 0) throw new FormatException($"{registers} registers cannot contain a whole number of {dataType} values.");
        var count = checked((ushort)(registers / width));
        return dataType switch
        {
            DataType.Bool => ToTagValue(original, dataType, await client.ReadUInt16Async(address, count), values => values.Select(value => value != 0).ToArray()),
            DataType.Int16 => ToTagValue(original, dataType, await client.ReadInt16Async(address, count)),
            DataType.UInt16 => ToTagValue(original, dataType, await client.ReadUInt16Async(address, count)),
            DataType.Int32 => ToTagValue(original, dataType, await client.ReadInt32Async(address, count)),
            DataType.UInt32 => ToTagValue(original, dataType, await client.ReadUInt32Async(address, count)),
            DataType.Int64 => ToTagValue(original, dataType, await client.ReadInt64Async(address, count)),
            DataType.UInt64 => ToTagValue(original, dataType, await client.ReadUInt64Async(address, count), values => values.Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            DataType.Float => ToTagValue(original, dataType, await client.ReadFloatAsync(address, count)),
            DataType.Double => ToTagValue(original, dataType, await client.ReadDoubleAsync(address, count)),
            _ => throw new NotSupportedException($"Unsupported Modbus data type {dataType}."),
        };
    }

    private static void ApplyOptions(ModbusRtu c, DeviceConnectionConfig config) { if (bool.TryParse(Get(config, "AddressStartWithZero"), out var zero)) c.AddressStartWithZero = zero; if (bool.TryParse(Get(config, "IsStringReverse"), out var rev)) c.IsStringReverse = rev; if (Enum.TryParse<DataFormat>(Get(config, "DataFormat"), true, out var df)) c.DataFormat = df; if (bool.TryParse(Get(config, "Crc16CheckEnable"), out var crc)) c.Crc16CheckEnable = crc; if (bool.TryParse(Get(config, "StationCheckMatch") ?? Get(config, "StationCheckMacth"), out var scm)) c.StationCheckMatch = scm; if (int.TryParse(Get(config, "BroadcastStation"), out var bs)) c.BroadcastStation = bs; }
    private static string Address(ParsedAddress p) => p.RegisterType == ModbusRegisterType.InputRegister ? $"x=4;{p.StartAddress}" : p.StartAddress.ToString();
    private void EnsureConnected() { if (_state != ConnectionState.Connected || _client is null) throw new InvalidOperationException("ModbusRTU driver is not connected"); }
    private void SetState(ConnectionState state, string? reason = null) { var old = _state; if (old == state) return; _state = state; StateChanged?.Invoke(this, new() { OldState = old, NewState = state, Reason = reason }); }
    private void CleanupClient() { try { _client?.Dispose(); } catch { } _client = null; }
    private static string? Get(DeviceConnectionConfig c, string key) => c.ExtendedProperties.TryGetValue(key, out var v) ? v : null;
    private static TagValue ToTagValue<T>(string address, DataType dataType, OperateResult<T> result) => result.IsSuccess ? new() { Address = address, DataType = dataType, Value = result.Content is ulong unsignedValue ? unsignedValue.ToString(System.Globalization.CultureInfo.InvariantCulture) : result.Content is byte[] bytes ? bytes.ToArray() : result.Content!, Quality = TagQuality.Good, Timestamp = DateTimeOffset.UtcNow } : BadTag(address, dataType, result.Message);
    private static TagValue ToTagValue<T>(string address, DataType dataType, OperateResult<T> result, Func<T, object> projector) => result.IsSuccess ? new() { Address = address, DataType = dataType, Value = projector(result.Content), Quality = TagQuality.Good, Timestamp = DateTimeOffset.UtcNow } : BadTag(address, dataType, result.Message);
    private static TagValue BadTag(string address, DataType dataType, string? error) => new() { Address = address, DataType = dataType, Value = dataType == DataType.String ? string.Empty : dataType == DataType.ByteArray ? Array.Empty<byte>() : dataType == DataType.Bool ? false : 0, Quality = TagQuality.Bad, Timestamp = DateTimeOffset.UtcNow, ErrorMessage = error };
}
