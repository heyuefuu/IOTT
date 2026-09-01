namespace IndustrialIoT.Protocols.HuazhongRobot;

using HslCommunication;
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
    private readonly HuazhongRobotAddressSpace _addressSpace;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ModbusTcpNet? _client;
    private ConnectionState _state = ConnectionState.Disconnected;

    public HuazhongRobotDriver(ILogger<HuazhongRobotDriver> logger, HuazhongRobotAddressSpace addressSpace)
    {
        _logger = logger;
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
        if (_state == ConnectionState.Connected) return new() { Success = true };
        SetState(ConnectionState.Connecting);
        try
        {
            var port = config.Port > 0 ? config.Port : DefaultPort;
            var station = byte.TryParse(config.ExtendedProperties.GetValueOrDefault("Station"), out var s) ? s : (byte)1;

            var client = new ModbusTcpNet(config.Host, port, station)
            {
                ConnectTimeOut = (int)config.ConnectTimeout.TotalMilliseconds,
                ReceiveTimeOut = (int)config.ReadTimeout.TotalMilliseconds,
            };
            var r = await client.ConnectServerAsync();
            if (!r.IsSuccess) throw new InvalidOperationException(r.Message);

            _client?.ConnectClose();
            _client = client;
            _logger.LogInformation("Huazhong Robot connected to {Host}:{Port} station={Station}", config.Host, port, station);
            SetState(ConnectionState.Connected);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            Cleanup();
            _logger.LogError(ex, "Huazhong Robot connection failed");
            SetState(ConnectionState.Faulted, ex.Message);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        Cleanup();
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_state != ConnectionState.Connected || _client is null) return false;
        try { return (await _client.ReadUInt16Async("100")).IsSuccess; }
        catch { return false; }
    }

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        var (modbusAddr, resolvedType, _) = _addressSpace.Resolve(address, dataType);
        if (_client is null) return Err(address, dataType, "Not connected");

        await _lock.WaitAsync(ct);
        try
        {
            var raw = await ReadByTypeAsync(modbusAddr, resolvedType);
            return raw is null
                ? Err(address, dataType, $"Read failed for {modbusAddr}")
                : new() { Address = address, DataType = dataType, Value = raw,
                          Quality = TagQuality.Good, Timestamp = DateTimeOffset.UtcNow };
        }
        catch (Exception ex) { return Err(address, dataType, ex.Message); }
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
        var (modbusAddr, resolvedType, writable) = _addressSpace.Resolve(address, dataType);
        if (!writable) return new() { Success = false, ErrorMessage = $"Address '{address}' is read-only" };
        if (_client is null) return new() { Success = false, ErrorMessage = "Not connected" };

        await _lock.WaitAsync(ct);
        try
        {
            var op = resolvedType switch
            {
                DataType.Bool => await _client.WriteAsync(modbusAddr, Convert.ToBoolean(value)),
                DataType.UInt16 => await _client.WriteAsync(modbusAddr, Convert.ToUInt16(value)),
                DataType.Int16 => await _client.WriteAsync(modbusAddr, Convert.ToInt16(value)),
                DataType.UInt32 => await _client.WriteAsync(modbusAddr, Convert.ToUInt32(value)),
                DataType.Int32 => await _client.WriteAsync(modbusAddr, Convert.ToInt32(value)),
                DataType.Float => await _client.WriteAsync(modbusAddr, Convert.ToSingle(value)),
                _ => new OperateResult { IsSuccess = false, Message = $"Unsupported type {resolvedType}" },
            };
            return new() { Success = op.IsSuccess, ErrorMessage = op.IsSuccess ? null : op.Message };
        }
        catch (Exception ex) { return new() { Success = false, ErrorMessage = ex.Message }; }
        finally { _lock.Release(); }
    }

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
        => Task.FromResult(_addressSpace.BuildTree());

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

    public ValueTask DisposeAsync() { Cleanup(); _lock.Dispose(); GC.SuppressFinalize(this); return ValueTask.CompletedTask; }

    private async Task<object?> ReadByTypeAsync(string addr, DataType t) => t switch
    {
        DataType.Bool => (await _client!.ReadBoolAsync(addr)) is { IsSuccess: true } b ? (object)b.Content : null,
        DataType.UInt16 => (await _client!.ReadUInt16Async(addr)) is { IsSuccess: true } x ? x.Content : null,
        DataType.Int16 => (await _client!.ReadInt16Async(addr)) is { IsSuccess: true } x ? x.Content : null,
        DataType.UInt32 => (await _client!.ReadUInt32Async(addr)) is { IsSuccess: true } x ? x.Content : null,
        DataType.Int32 => (await _client!.ReadInt32Async(addr)) is { IsSuccess: true } x ? x.Content : null,
        DataType.Float => (await _client!.ReadFloatAsync(addr)) is { IsSuccess: true } x ? x.Content : null,
        _ => null,
    };

    private static TagValue Err(string addr, DataType t, string msg) => new()
    {
        Address = addr, DataType = t, Value = string.Empty,
        Quality = TagQuality.Bad, Timestamp = DateTimeOffset.UtcNow, ErrorMessage = msg,
    };

    private void Cleanup() { _client?.ConnectClose(); _client = null; }

    private void SetState(ConnectionState next, string? reason = null)
    {
        var old = _state;
        if (old == next) return;
        _state = next;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs { OldState = old, NewState = next, Reason = reason });
    }
}
