namespace IndustrialIoT.Protocols.Inovance;

using System.IO.Ports;
using System.Text;
using HslCommunication;
using HslCommunication.Core;
using HslCommunication.Profinet.Inovance;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

[ProtocolDriver(ProtocolType.InovanceSerial, "Inovance", "汇川")]
public sealed class InovanceSerialDriver : IProtocolDriver, IAddressSpaceBrowser
{
    private const ushort DefaultStringLength = 16;
    private readonly ILogger<InovanceSerialDriver> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private IInovanceClient? _client;
    private ConnectionState _state = ConnectionState.Disconnected;
    private InovanceSeries? _series;

    public InovanceSerialDriver(ILogger<InovanceSerialDriver> logger) => _logger = logger;
    public ProtocolType Protocol => ProtocolType.InovanceSerial;
    public ConnectionState State => _state;
    public DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.Write | DriverCapabilities.Browse | DriverCapabilities.BatchRead;
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        if (_state == ConnectionState.Connected) return new() { Success = true };
        SetState(ConnectionState.Connecting);
        try
        {
            _series = InovanceAddressSpace.ParseSeriesOrThrow(Get(config, "Series"));
            var station = byte.TryParse(Get(config, "Station"), out var s) ? s : (byte)1;
            var inner = new InovanceSerial(_series.Value) { Station = station };
            if (bool.TryParse(Get(config, "AddressStartWithZero"), out var zero)) inner.AddressStartWithZero = zero;
            if (bool.TryParse(Get(config, "IsStringReverse"), out var reverse)) inner.IsStringReverse = reverse;
            if (Enum.TryParse<DataFormat>(Get(config, "DataFormat"), true, out var format)) inner.ByteTransform.DataFormat = format;

            var portName = Get(config, "PortName") ?? throw new InvalidOperationException("Inovance serial requires ExtendedProperties['PortName'].");
            var baud = int.TryParse(Get(config, "BaudRate"), out var b) ? b : 9600;
            var dataBits = int.TryParse(Get(config, "DataBits"), out var d) ? d : 8;
            var stopBits = Enum.TryParse<StopBits>(Get(config, "StopBits"), true, out var sb) ? sb : StopBits.One;
            var parity = Enum.TryParse<Parity>(Get(config, "Parity"), true, out var p) ? p : Parity.None;
            inner.SerialPortInni(sp => { sp.PortName = portName; sp.BaudRate = baud; sp.DataBits = dataBits; sp.StopBits = stopBits; sp.Parity = parity; });

            var open = await Task.Run(() => inner.Open(), ct);
            if (!open.IsSuccess) throw new InvalidOperationException(open.Message);

            _client?.Dispose();
            _client = new HslInovanceSerialClientAdapter(inner);
            _logger.LogInformation("Inovance serial connected on {Port} @ {Baud}", portName, baud);
            SetState(ConnectionState.Connected);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            CleanupClient();
            _logger.LogError(ex, "Inovance serial connect failed");
            SetState(ConnectionState.Faulted, ex.Message);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_client is not null) { try { await _client.ConnectCloseAsync(); } catch { } }
        CleanupClient();
        _series = null;
        SetState(ConnectionState.Disconnected);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_state != ConnectionState.Connected || _client is null || !_series.HasValue) return false;
        await _semaphore.WaitAsync(ct);
        try { return (await _client.ReadUInt16Async(InovanceAddressSpace.GetPingAddress(_series.Value))).IsSuccess; }
        catch { return false; }
        finally { _semaphore.Release(); }
    }

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        EnsureConnected();
        var mapped = InovanceAddressSpace.Normalize(address, RequireSeries());
        await _semaphore.WaitAsync(ct);
        try { return await ReadCoreAsync(address, mapped, dataType); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Inovance serial read failed at {Address}", address); return BadTag(address, dataType, ex.Message); }
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
        var mapped = InovanceAddressSpace.Normalize(address, RequireSeries());
        await _semaphore.WaitAsync(ct);
        try
        {
            var client = _client!;
            var result = dataType switch
            {
                DataType.Bool => await client.WriteAsync(mapped, Convert.ToBoolean(value)), DataType.Int16 => await client.WriteAsync(mapped, Convert.ToInt16(value)), DataType.UInt16 => await client.WriteAsync(mapped, Convert.ToUInt16(value)),
                DataType.Int32 => await client.WriteAsync(mapped, Convert.ToInt32(value)), DataType.UInt32 => await client.WriteAsync(mapped, Convert.ToUInt32(value)), DataType.Float => await client.WriteAsync(mapped, Convert.ToSingle(value)),
                DataType.Int64 => await client.WriteAsync(mapped, Convert.ToInt64(value)), DataType.Double => await client.WriteAsync(mapped, Convert.ToDouble(value)), DataType.String => await client.WriteAsync(mapped, Convert.ToString(value) ?? string.Empty, DefaultStringLength, Encoding.ASCII),
                DataType.ByteArray => await client.WriteAsync(mapped, (byte[])value), _ => await client.WriteAsync(mapped, Convert.ToUInt16(value)),
            };
            return result.IsSuccess ? new() { Success = true } : new() { Success = false, ErrorMessage = result.Message };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Inovance serial write failed at {Address}", address); return new() { Success = false, ErrorMessage = ex.Message }; }
        finally { _semaphore.Release(); }
    }

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default) => Task.FromResult(InovanceAddressSpace.Browse(_series, parentPath));
    public Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var lines = InovanceAddressSpace.Export(_series).Select(x => $"{x.Path},{x.Path},{x.DataType},True,{x.Writable}");
        var csv = "Path,DisplayName,DataType,Readable,Writable\n" + string.Join(Environment.NewLine, lines);
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(csv)));
    }
    public async ValueTask DisposeAsync() { await DisconnectAsync(); _semaphore.Dispose(); GC.SuppressFinalize(this); }

    private async Task<TagValue> ReadCoreAsync(string address, string mapped, DataType dataType)
    {
        var client = _client!;
        return dataType switch
        {
            DataType.Bool => ToTagValue(address, dataType, await client.ReadBoolAsync(mapped)), DataType.Int16 => ToTagValue(address, dataType, await client.ReadInt16Async(mapped)), DataType.UInt16 => ToTagValue(address, dataType, await client.ReadUInt16Async(mapped)),
            DataType.Int32 => ToTagValue(address, dataType, await client.ReadInt32Async(mapped)), DataType.UInt32 => ToTagValue(address, dataType, await client.ReadUInt32Async(mapped)), DataType.Float => ToTagValue(address, dataType, await client.ReadFloatAsync(mapped)),
            DataType.Int64 => ToTagValue(address, dataType, await client.ReadInt64Async(mapped)), DataType.Double => ToTagValue(address, dataType, await client.ReadDoubleAsync(mapped)), DataType.String => ToTagValue(address, dataType, await client.ReadStringAsync(mapped, DefaultStringLength, Encoding.ASCII)),
            DataType.ByteArray => ToTagValue(address, dataType, await client.ReadAsync(mapped, 1)), _ => ToTagValue(address, dataType, await client.ReadUInt16Async(mapped)),
        };
    }

    private InovanceSeries RequireSeries() => _series ?? throw new InvalidOperationException("Inovance Series is required.");
    private void EnsureConnected() { if (_state != ConnectionState.Connected || _client is null) throw new InvalidOperationException("Inovance serial driver is not connected"); }
    private void SetState(ConnectionState state, string? reason = null) { var old = _state; if (old == state) return; _state = state; StateChanged?.Invoke(this, new() { OldState = old, NewState = state, Reason = reason }); }
    private void CleanupClient() { try { _client?.Dispose(); } catch { } _client = null; }
    private static string? Get(DeviceConnectionConfig config, string key) => InovanceAddressSpace.GetProperty(config, key);
    private static TagValue ToTagValue<T>(string address, DataType dataType, OperateResult<T> result) => result.IsSuccess ? new() { Address = address, DataType = dataType, Value = result.Content is byte[] bytes ? bytes.ToArray() : result.Content!, Quality = TagQuality.Good, Timestamp = DateTimeOffset.UtcNow } : BadTag(address, dataType, result.Message);
    private static TagValue BadTag(string address, DataType dataType, string? error) => new() { Address = address, DataType = dataType, Value = dataType == DataType.String ? string.Empty : dataType == DataType.ByteArray ? Array.Empty<byte>() : dataType == DataType.Bool ? false : 0, Quality = TagQuality.Bad, Timestamp = DateTimeOffset.UtcNow, ErrorMessage = error };
}
