namespace IndustrialIoT.Protocols.Inovance;

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

[ProtocolDriver(ProtocolType.InovanceSerialOverTcp, "Inovance", "汇川")]
public sealed class InovanceSerialOverTcpDriver : IProtocolDriver, IAddressSpaceBrowser
{
    private const ushort DefaultStringLength = 16;
    private readonly ILogger<InovanceSerialOverTcpDriver> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private IInovanceClient? _client;
    private ConnectionState _state = ConnectionState.Disconnected;
    private InovanceSeries? _series;

    public InovanceSerialOverTcpDriver(ILogger<InovanceSerialOverTcpDriver> logger) => _logger = logger;
    public ProtocolType Protocol => ProtocolType.InovanceSerialOverTcp;
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
            var inner = new InovanceSerialOverTcp { Station = station, IpAddress = config.Host, Port = config.Port, Series = _series.Value };
            inner.ConnectTimeOut = (int)config.ConnectTimeout.TotalMilliseconds;
            inner.ReceiveTimeOut = (int)config.ReadTimeout.TotalMilliseconds;
            if (bool.TryParse(Get(config, "AddressStartWithZero"), out var zero)) inner.AddressStartWithZero = zero;
            if (bool.TryParse(Get(config, "IsStringReverse"), out var reverse)) inner.IsStringReverse = reverse;
            if (Enum.TryParse<DataFormat>(Get(config, "DataFormat"), true, out var format)) inner.ByteTransform.DataFormat = format;

            var result = await inner.ConnectServerAsync();
            if (!result.IsSuccess) throw new InvalidOperationException(result.Message);

            _client?.Dispose();
            _client = new HslInovanceSerialOverTcpClientAdapter(inner);
            _logger.LogInformation("Inovance serial-over-TCP connected to {Host}:{Port} station {Station}", config.Host, config.Port, station);
            SetState(ConnectionState.Connected);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            CleanupClient();
            _logger.LogError(ex, "Inovance serial-over-TCP connect failed");
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
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Inovance serial-over-TCP read failed at {Address}", address); return BadTag(address, dataType, ex.Message); }
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
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Inovance serial-over-TCP write failed at {Address}", address); return new() { Success = false, ErrorMessage = ex.Message }; }
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
    private void EnsureConnected() { if (_state != ConnectionState.Connected || _client is null) throw new InvalidOperationException("Inovance serial-over-TCP driver is not connected"); }
    private void SetState(ConnectionState state, string? reason = null) { var old = _state; if (old == state) return; _state = state; StateChanged?.Invoke(this, new() { OldState = old, NewState = state, Reason = reason }); }
    private void CleanupClient() { try { _client?.Dispose(); } catch { } _client = null; }
    private static string? Get(DeviceConnectionConfig config, string key) => InovanceAddressSpace.GetProperty(config, key);
    private static TagValue ToTagValue<T>(string address, DataType dataType, OperateResult<T> result) => result.IsSuccess ? new() { Address = address, DataType = dataType, Value = result.Content is byte[] bytes ? bytes.ToArray() : result.Content!, Quality = TagQuality.Good, Timestamp = DateTimeOffset.UtcNow } : BadTag(address, dataType, result.Message);
    private static TagValue BadTag(string address, DataType dataType, string? error) => new() { Address = address, DataType = dataType, Value = dataType == DataType.String ? string.Empty : dataType == DataType.ByteArray ? Array.Empty<byte>() : dataType == DataType.Bool ? false : 0, Quality = TagQuality.Bad, Timestamp = DateTimeOffset.UtcNow, ErrorMessage = error };
}

internal sealed class HslInovanceSerialOverTcpClientAdapter : IInovanceClient
{
    public InovanceSerialOverTcp Inner { get; }
    public HslInovanceSerialOverTcpClientAdapter(InovanceSerialOverTcp inner) => Inner = inner;

    public Task<OperateResult> ConnectServerAsync() => Inner.ConnectServerAsync();
    public Task ConnectCloseAsync() => Inner.ConnectCloseAsync();
    public Task<OperateResult<byte[]>> ReadAsync(string address, ushort length) => Inner.ReadAsync(address, length);
    public Task<OperateResult<bool>> ReadBoolAsync(string address) => Inner.ReadBoolAsync(address);
    public Task<OperateResult<short>> ReadInt16Async(string address) => Inner.ReadInt16Async(address);
    public Task<OperateResult<ushort>> ReadUInt16Async(string address) => Inner.ReadUInt16Async(address);
    public Task<OperateResult<int>> ReadInt32Async(string address) => Inner.ReadInt32Async(address);
    public Task<OperateResult<uint>> ReadUInt32Async(string address) => Inner.ReadUInt32Async(address);
    public Task<OperateResult<long>> ReadInt64Async(string address) => Inner.ReadInt64Async(address);
    public Task<OperateResult<double>> ReadDoubleAsync(string address) => Inner.ReadDoubleAsync(address);
    public Task<OperateResult<float>> ReadFloatAsync(string address) => Inner.ReadFloatAsync(address);
    public Task<OperateResult<string>> ReadStringAsync(string address, ushort length, Encoding encoding) => Inner.ReadStringAsync(address, length, encoding);
    public Task<OperateResult> WriteAsync(string address, bool value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, short value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, ushort value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, int value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, uint value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, long value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, double value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, float value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, byte[] value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, string value, int length, Encoding encoding) => Inner.WriteAsync(address, value, length, encoding);
    public void Dispose() => Inner.Dispose();
}
