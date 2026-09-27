namespace IndustrialIoT.Protocols.HuazhongRobot;

using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HslCommunication;
using HslCommunication.Core;
using HslCommunication.Core.Pipe;
using HslCommunication.ModBus;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

/// <summary>
/// 华中机器人驱动（HR / HSR / HC 系列）。底层走 Modbus/TCP，映射由 <see cref="HuazhongRobotAddressSpace"/> 提供。
/// 地址可用标准路径（"/Robot/Joint/J1"）或直传 Hsl 原地址（"1000;float"、"0x0040"、"102"）。
/// </summary>
[ProtocolDriver(ProtocolType.HuazhongRobot, "华中数控", "华中机器人", "HSR", "HR", "HC", "HNC-Robot")]
public sealed class HuazhongRobotDriver : IProtocolDriver, IAddressSpaceBrowser
{
    private const int DefaultPort = 502;
    private readonly ILogger<HuazhongRobotDriver> _logger;
    private readonly HuazhongRobotAddressSpace _defaultAddressSpace;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private HuazhongRobotAddressSpace _addressSpace;
    private ModbusTcpNet? _client;
    private string? _pingAddress;
    private DataType _pingDataType = DataType.UInt16;
    private string? _lastReadAddress;
    private DataType _lastReadDataType;
    private bool _communicationHealthy;
    private ushort _stringLength = 16;
    private ushort _byteArrayLength = 2;
    private bool _disposed;
    private ConnectionState _state = ConnectionState.Disconnected;

    public HuazhongRobotDriver(ILogger<HuazhongRobotDriver> logger, HuazhongRobotAddressSpace addressSpace)
    {
        _logger = logger;
        _defaultAddressSpace = addressSpace;
        _addressSpace = addressSpace;
    }
    public ProtocolType Protocol => ProtocolType.HuazhongRobot;
    public ConnectionState State => _state;
    public DriverCapabilities Capabilities =>
        DriverCapabilities.Read | DriverCapabilities.Write |
        DriverCapabilities.Browse | DriverCapabilities.BatchRead;
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state == ConnectionState.Connected) return new() { Success = true };
            SetState(ConnectionState.Connecting);
            var port = config.Port > 0 ? config.Port : DefaultPort;
            var station = byte.TryParse(config.ExtendedProperties.GetValueOrDefault("Station"), out var s) ? s : (byte)1;
            if (config.ExtendedProperties.TryGetValue("Station", out var stationText)
                && (!byte.TryParse(stationText, out station) || station == 0))
                throw new ArgumentException("Station must be between 1 and 255.");
            _addressSpace = LoadAddressSpace(config.ExtendedProperties);
            _pingAddress = config.ExtendedProperties.GetValueOrDefault("PingAddress");
            if (string.IsNullOrWhiteSpace(_pingAddress)) _pingAddress = _addressSpace.All.FirstOrDefault()?.Path;
            var pingNode = _addressSpace.All.FirstOrDefault(node =>
                node.Path.Equals(_pingAddress, StringComparison.OrdinalIgnoreCase));
            _pingDataType = Enum.TryParse<DataType>(config.ExtendedProperties.GetValueOrDefault("PingDataType"), true, out var pingType)
                && Enum.IsDefined(pingType) ? pingType : pingNode?.DataType ?? DataType.UInt16;
            _stringLength = ParseLength(config.ExtendedProperties, "StringLength", 16);
            _byteArrayLength = ParseLength(config.ExtendedProperties, "ByteArrayLength", 2);
            if (_byteArrayLength % 2 != 0) throw new ArgumentException("ByteArrayLength must contain an even number of bytes.");
            _lastReadAddress = null;

            var client = new ModbusTcpNet(config.Host, port, station)
            {
                ConnectTimeOut = (int)config.ConnectTimeout.TotalMilliseconds,
                ReceiveTimeOut = (int)config.ReadTimeout.TotalMilliseconds,
            };
            var dataFormat = config.ExtendedProperties.GetValueOrDefault("DataFormat");
            if (!string.IsNullOrWhiteSpace(dataFormat))
            {
                if (!Enum.TryParse<DataFormat>(dataFormat, true, out var format) || !Enum.IsDefined(format))
                    throw new ArgumentException($"Invalid DataFormat '{dataFormat}'.");
                client.DataFormat = format;
            }
            _client?.ConnectClose();
            _client = client;
            var r = await client.ConnectServerAsync();
            if (!r.IsSuccess) throw new InvalidOperationException(r.Message);
            _communicationHealthy = true;
            _logger.LogInformation("Huazhong Robot connected to {Host}:{Port} station={Station}", config.Host, port, station);
            SetState(ConnectionState.Connected);
            return new() { Success = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Cleanup();
            _logger.LogError(ex, "Huazhong Robot connection failed");
            SetState(ConnectionState.Faulted, ex.Message);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
        finally { _lock.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { Cleanup(); SetState(ConnectionState.Disconnected); }
        finally { _lock.Release(); }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        string? address;
        DataType dataType;
        await _lock.WaitAsync(ct);
        try
        {
            if (_state != ConnectionState.Connected || _client is null) return false;
            if (!TransportIsHealthy())
            {
                SetState(ConnectionState.Faulted, "Modbus connection closed");
                return false;
            }
            address = _pingAddress ?? _lastReadAddress;
            if (address is null) return _communicationHealthy;
            dataType = _pingAddress is null ? _lastReadDataType : _pingDataType;
        }
        finally { _lock.Release(); }
        try { return (await ReadTagAsync(address, dataType, ct)).Quality == TagQuality.Good; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_client is null || _state != ConnectionState.Connected) return Err(address, dataType, "Not connected");
            var (modbusAddr, resolvedType, _) = _addressSpace.Resolve(address, dataType);
            if (resolvedType != dataType) return Err(address, dataType, $"Configured type is {resolvedType}; requested {dataType}.");
            var raw = await ReadByTypeAsync(modbusAddr, resolvedType);
            UpdateTransportHealth(raw is not null);
            if (raw is not null) { _lastReadAddress = address; _lastReadDataType = dataType; }
            return raw is null
                ? Err(address, dataType, $"Read failed for {modbusAddr}")
                : new() { Address = address, DataType = dataType, Value = raw,
                          Quality = TagQuality.Good, Timestamp = DateTimeOffset.UtcNow };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Err(address, dataType, ex.Message); }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        var results = new List<TagValue>(requests.Count);
        foreach (var r in requests) results.Add(await ReadTagAsync(r.Address, r.DataType, ct));
        return results;
    }

    public async Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_client is null || _state != ConnectionState.Connected)
                return new() { Success = false, ErrorMessage = "Not connected" };
            var (modbusAddr, resolvedType, writable) = _addressSpace.Resolve(address, dataType);
            if (resolvedType != dataType)
                return new() { Success = false, ErrorMessage = $"Configured type is {resolvedType}; requested {dataType}." };
            if (!writable) return new() { Success = false, ErrorMessage = $"Address '{address}' is read-only" };
            if (resolvedType == DataType.ByteArray && (value is not byte[] bytes || bytes.Length == 0 || bytes.Length % 2 != 0))
                return new() { Success = false, ErrorMessage = "ByteArray must contain a positive, even number of bytes." };
            var op = resolvedType switch
            {
                DataType.Bool => await _client.WriteAsync(modbusAddr, Convert.ToBoolean(value)),
                DataType.UInt16 => await _client.WriteAsync(modbusAddr, Convert.ToUInt16(value)),
                DataType.Int16 => await _client.WriteAsync(modbusAddr, Convert.ToInt16(value)),
                DataType.UInt32 => await _client.WriteAsync(modbusAddr, Convert.ToUInt32(value)),
                DataType.Int32 => await _client.WriteAsync(modbusAddr, Convert.ToInt32(value)),
                DataType.Float => await _client.WriteAsync(modbusAddr, Convert.ToSingle(value)),
                DataType.Int64 => await _client.WriteAsync(modbusAddr, Convert.ToInt64(value)),
                DataType.UInt64 => await _client.WriteAsync(modbusAddr, Convert.ToUInt64(value)),
                DataType.Double => await _client.WriteAsync(modbusAddr, Convert.ToDouble(value)),
                DataType.String => await WriteStringAsync(modbusAddr, Convert.ToString(value) ?? string.Empty),
                DataType.ByteArray => await _client.WriteAsync(modbusAddr, (byte[])value),
                _ => new OperateResult { IsSuccess = false, Message = $"Unsupported type {resolvedType}" },
            };
            UpdateTransportHealth(op.IsSuccess);
            return new() { Success = op.IsSuccess, ErrorMessage = op.IsSuccess ? null : op.Message };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new() { Success = false, ErrorMessage = ex.Message }; }
        finally { _lock.Release(); }
    }

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        var roots = _addressSpace.BuildTree();
        return Task.FromResult<IReadOnlyList<AddressNode>>(string.IsNullOrEmpty(parentPath)
            ? roots : roots.FirstOrDefault(node => node.Path.Equals(parentPath, StringComparison.OrdinalIgnoreCase))?.Children ?? []);
    }

    public async Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var ms = new MemoryStream();
        var text = format == ExportFormat.JSON
            ? System.Text.Json.JsonSerializer.Serialize(_addressSpace.All, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
            : "Path,DisplayName,ModbusAddress,DataType,IsWritable\n" +
              string.Join("\n", _addressSpace.All.Select(n => $"{n.Path},{n.DisplayName},{n.ModbusAddress},{n.DataType},{n.IsWritable}"));
        await ms.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), ct);
        ms.Position = 0;
        return ms;
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync();
        try { _disposed = true; Cleanup(); SetState(ConnectionState.Disconnected); }
        finally { _lock.Release(); }
        GC.SuppressFinalize(this);
    }

    private async Task<object?> ReadByTypeAsync(string addr, DataType t) => t switch
    {
        DataType.Bool => (await _client!.ReadBoolAsync(addr)) is { IsSuccess: true } b ? (object)b.Content : null,
        DataType.UInt16 => (await _client!.ReadUInt16Async(addr)) is { IsSuccess: true } x ? x.Content : null,
        DataType.Int16 => (await _client!.ReadInt16Async(addr)) is { IsSuccess: true } x ? x.Content : null,
        DataType.UInt32 => (await _client!.ReadUInt32Async(addr)) is { IsSuccess: true } x ? x.Content : null,
        DataType.Int32 => (await _client!.ReadInt32Async(addr)) is { IsSuccess: true } x ? x.Content : null,
        DataType.Float => (await _client!.ReadFloatAsync(addr)) is { IsSuccess: true } x ? x.Content : null,
        DataType.Int64 => (await _client!.ReadInt64Async(addr)) is { IsSuccess: true } x ? x.Content : null,
        DataType.UInt64 => (await _client!.ReadUInt64Async(addr)) is { IsSuccess: true } x ? x.Content.ToString(CultureInfo.InvariantCulture) : null,
        DataType.Double => (await _client!.ReadDoubleAsync(addr)) is { IsSuccess: true } x ? x.Content : null,
        DataType.String => await ReadStringAsync(addr),
        DataType.ByteArray => (await _client!.ReadAsync(addr, (ushort)(_byteArrayLength / 2))) is { IsSuccess: true } x ? x.Content : null,
        _ => null,
    };

    private async Task<string?> ReadStringAsync(string address)
    {
        var result = await _client!.ReadAsync(address, (ushort)((_stringLength + 1) / 2));
        return result.IsSuccess
            ? _client.ByteTransform.TransString(result.Content, 0, _stringLength, Encoding.UTF8)
            : null;
    }

    private Task<OperateResult> WriteStringAsync(string address, string value)
    {
        if (_stringLength > 246)
            return Task.FromResult(new OperateResult { IsSuccess = false,
                Message = "String writes support at most 246 bytes (123 Modbus registers)." });
        var byteCount = 0;
        var charCount = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (byteCount + rune.Utf8SequenceLength > _stringLength) break;
            byteCount += rune.Utf8SequenceLength;
            charCount += rune.Utf16SequenceLength;
        }
        var bytes = _client!.ByteTransform.TransByte(value[..charCount], (_stringLength + 1) / 2 * 2, Encoding.UTF8);
        return _client.WriteAsync(address, bytes);
    }

    private static TagValue Err(string addr, DataType t, string msg) => new()
    {
        Address = addr, DataType = t, Value = string.Empty,
        Quality = TagQuality.Bad, Timestamp = DateTimeOffset.UtcNow, ErrorMessage = msg,
    };

    private HuazhongRobotAddressSpace LoadAddressSpace(Dictionary<string, string> properties)
    {
        if (!properties.TryGetValue("AddressMap", out var json) || string.IsNullOrWhiteSpace(json))
            return _defaultAddressSpace;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var nodes = JsonSerializer.Deserialize<List<HuazhongRobotAddressSpace.Node>>(json, options)
            ?? throw new ArgumentException("AddressMap must be a JSON array.");
        return new HuazhongRobotAddressSpace(nodes);
    }

    private static ushort ParseLength(Dictionary<string, string> properties, string key, ushort fallback)
    {
        if (!properties.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text)) return fallback;
        if (!ushort.TryParse(text, out var length) || length is < 1 or > 250)
            throw new ArgumentException($"{key} must be between 1 and 250 bytes.");
        return length;
    }

    private bool TransportIsHealthy()
    {
        try
        {
            var pipe = _client?.CommunicationPipe as PipeTcpNet;
            var socket = pipe?.Socket;
            return pipe is not null && !pipe.IsConnectError() && socket is { Connected: true }
                && !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return false; }
    }

    private void UpdateTransportHealth(bool succeeded)
    {
        _communicationHealthy = succeeded;
        if (!succeeded && !TransportIsHealthy())
            SetState(ConnectionState.Faulted, "Modbus communication failed");
    }

    private void Cleanup() { _client?.ConnectClose(); _client = null; _communicationHealthy = false; }

    private void SetState(ConnectionState next, string? reason = null)
    {
        var old = _state;
        if (old == next) return;
        _state = next;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs { OldState = old, NewState = next, Reason = reason });
    }
}
