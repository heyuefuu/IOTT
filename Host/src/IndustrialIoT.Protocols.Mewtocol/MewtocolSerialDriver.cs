namespace IndustrialIoT.Protocols.Mewtocol;

using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using HslCommunication;
using HslCommunication.Core;
using HslCommunication.Profinet.Panasonic;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

[ProtocolDriver(ProtocolType.MewtocolSerial, "Panasonic", "松下", "FP")]
public sealed class MewtocolSerialDriver : IProtocolDriver, IAddressSpaceBrowser
{
    private static readonly Regex NumericAddressRegex = new(
        @"^(?<prefix>DT|SR|X|Y|R|T|C)(?<number>\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NativeAddressRegex = new(
        @"^(?<prefix>SR|X|Y|R|T|C|D|LD|L|F)\d+(?:\.[0-9A-F]+|[A-F])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const ushort DefaultStringLength = 16;

    private readonly ILogger<MewtocolSerialDriver> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private PanasonicMewtocol? _client;
    private ConnectionState _state = ConnectionState.Disconnected;
    private byte _station = 238;

    public MewtocolSerialDriver(ILogger<MewtocolSerialDriver> logger) => _logger = logger;
    public ProtocolType Protocol => ProtocolType.MewtocolSerial;
    public ConnectionState State => _state;
    public DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.Write | DriverCapabilities.Browse | DriverCapabilities.BatchRead;
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        if (_state == ConnectionState.Connected) return new() { Success = true };
        SetState(ConnectionState.Connecting);
        _station = byte.TryParse(Get(config, "Station"), out var st) ? st : (byte)238;

        try
        {
            var client = new PanasonicMewtocol(_station);
            if (Enum.TryParse<DataFormat>(Get(config, "DataFormat"), true, out var df)) client.ByteTransform.DataFormat = df;
            if (bool.TryParse(Get(config, "IsStringReverseByteWord"), out var rev)) client.ByteTransform.IsStringReverseByteWord = rev;

            var portName = Get(config, "PortName") ?? throw new InvalidOperationException("Mewtocol serial requires ExtendedProperties['PortName'].");
            var baud = int.TryParse(Get(config, "BaudRate"), out var b) ? b : 9600;
            var dataBits = int.TryParse(Get(config, "DataBits"), out var d) ? d : 8;
            var stopBits = Enum.TryParse<StopBits>(Get(config, "StopBits"), true, out var sb) ? sb : StopBits.One;
            var parity = Enum.TryParse<Parity>(Get(config, "Parity"), true, out var p) ? p : Parity.Odd;
            client.SerialPortInni(sp => { sp.PortName = portName; sp.BaudRate = baud; sp.DataBits = dataBits; sp.StopBits = stopBits; sp.Parity = parity; });

            var open = await Task.Run(() => client.Open(), ct);
            if (!open.IsSuccess) throw new InvalidOperationException(open.Message);

            _client?.Dispose();
            _client = client;
            _logger.LogInformation("Mewtocol serial connected on {Port} @ {Baud} station {Station}", portName, baud, _station);
            SetState(ConnectionState.Connected);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            CleanupClient();
            _logger.LogError(ex, "Mewtocol serial connect failed");
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
        try { return (await _client.ReadUInt16Async("D0")).IsSuccess; }
        catch { return false; }
        finally { _semaphore.Release(); }
    }

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        EnsureConnected();
        var mapped = MapAddress(address);
        await _semaphore.WaitAsync(ct);
        try
        {
            var c = _client!;
            var body = mapped.ToUpperInvariant();
            if (dataType == DataType.Bool && StartsWithAny(body, "X", "Y", "R", "L", "LD", "D", "F"))
                return ToTagValue(address, dataType, await c.ReadBoolAsync(mapped));

            if (IsTimerOrCounterAddress(body))
            {
                var tc = await c.ReadUInt16Async(mapped);
                if (!tc.IsSuccess) return BadTag(address, dataType, tc.Message);
                object tcValue = dataType == DataType.Bool ? tc.Content > 0 : tc.Content;
                return new() { Address = address, DataType = dataType, Value = tcValue, Quality = TagQuality.Good, Timestamp = DateTimeOffset.UtcNow };
            }

            return dataType switch
            {
                DataType.Bool => ToTagValue(address, dataType, await c.ReadUInt16Async(mapped), v => v > 0),
                DataType.Int16 => ToTagValue(address, dataType, await c.ReadInt16Async(mapped)),
                DataType.UInt16 => ToTagValue(address, dataType, await c.ReadUInt16Async(mapped)),
                DataType.Int32 => ToTagValue(address, dataType, await c.ReadInt32Async(mapped)),
                DataType.UInt32 => ToTagValue(address, dataType, await c.ReadUInt32Async(mapped)),
                DataType.Float => ToTagValue(address, dataType, await c.ReadFloatAsync(mapped)),
                DataType.Int64 => ToTagValue(address, dataType, await c.ReadInt64Async(mapped)),
                DataType.Double => ToTagValue(address, dataType, await c.ReadDoubleAsync(mapped)),
                DataType.String => ToTagValue(address, dataType, await c.ReadStringAsync(mapped, DefaultStringLength, Encoding.ASCII)),
                DataType.ByteArray => ToTagValue(address, dataType, await c.ReadAsync(mapped, 1)),
                _ => ToTagValue(address, dataType, await c.ReadUInt16Async(mapped)),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Mewtocol serial read failed at {Address}", address); return BadTag(address, dataType, ex.Message); }
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
        var mapped = MapAddress(address);
        await _semaphore.WaitAsync(ct);
        try
        {
            var c = _client!;
            var body = mapped.ToUpperInvariant();
            OperateResult result;
            if (dataType == DataType.Bool && StartsWithAny(body, "X", "Y", "R", "L", "LD", "D", "F"))
            {
                result = await c.WriteAsync(mapped, Convert.ToBoolean(value));
            }
            else if (IsTimerOrCounterAddress(body))
            {
                result = await c.WriteAsync(mapped, Convert.ToUInt16(value));
            }
            else
            {
                result = dataType switch
                {
                    DataType.Bool => await c.WriteAsync(mapped, Convert.ToUInt16(Convert.ToBoolean(value) ? 1 : 0)),
                    DataType.Int16 => await c.WriteAsync(mapped, Convert.ToInt16(value)),
                    DataType.UInt16 => await c.WriteAsync(mapped, Convert.ToUInt16(value)),
                    DataType.Int32 => await c.WriteAsync(mapped, Convert.ToInt32(value)),
                    DataType.UInt32 => await c.WriteAsync(mapped, Convert.ToUInt32(value)),
                    DataType.Float => await c.WriteAsync(mapped, Convert.ToSingle(value)),
                    DataType.Int64 => await c.WriteAsync(mapped, Convert.ToInt64(value)),
                    DataType.Double => await c.WriteAsync(mapped, Convert.ToDouble(value)),
                    DataType.String => await c.WriteAsync(mapped, Convert.ToString(value) ?? string.Empty, DefaultStringLength, Encoding.ASCII),
                    DataType.ByteArray => await c.WriteAsync(mapped, (byte[])value),
                    _ => await c.WriteAsync(mapped, Convert.ToUInt16(value)),
                };
            }
            return result.IsSuccess ? new() { Success = true } : new() { Success = false, ErrorMessage = result.Message };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Mewtocol serial write failed at {Address}", address); return new() { Success = false, ErrorMessage = ex.Message }; }
        finally { _semaphore.Release(); }
    }

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        var driver = new MewtocolDriver(_browseLogger);
        return driver.BrowseAsync(parentPath, ct);
    }

    public Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var driver = new MewtocolDriver(_browseLogger);
        return driver.ExportAddressSpaceAsync(format, ct);
    }

    public async ValueTask DisposeAsync() { await DisconnectAsync(); _semaphore.Dispose(); GC.SuppressFinalize(this); }

    private static readonly ILogger<MewtocolDriver> _browseLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<MewtocolDriver>.Instance;

    private static string MapAddress(string address)
    {
        var body = address.Trim().ToUpperInvariant();
        var numericMatch = NumericAddressRegex.Match(body);
        if (numericMatch.Success)
        {
            var prefix = numericMatch.Groups["prefix"].Value;
            var number = numericMatch.Groups["number"].Value;
            return prefix == "DT" ? $"D{number}" : $"{prefix}{number}";
        }
        if (NativeAddressRegex.IsMatch(body)) return body;
        throw new MewtocolException($"Invalid Mewtocol address: '{address}'.");
    }

    private static bool StartsWithAny(string s, params string[] prefixes)
    {
        foreach (var p in prefixes) if (s.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsTimerOrCounterAddress(string body) =>
        body.StartsWith("T", StringComparison.Ordinal) || body.StartsWith("C", StringComparison.Ordinal);

    private void EnsureConnected() { if (_state != ConnectionState.Connected || _client is null) throw new InvalidOperationException("Mewtocol serial driver is not connected"); }
    private void SetState(ConnectionState state, string? reason = null) { var old = _state; if (old == state) return; _state = state; StateChanged?.Invoke(this, new() { OldState = old, NewState = state, Reason = reason }); }
    private void CleanupClient() { try { _client?.Dispose(); } catch { } _client = null; }
    private static string? Get(DeviceConnectionConfig config, string key) => config.ExtendedProperties.TryGetValue(key, out var v) ? v : null;

    private static TagValue ToTagValue<T>(string address, DataType dataType, OperateResult<T> result) => result.IsSuccess
        ? new() { Address = address, DataType = dataType, Value = result.Content is byte[] bytes ? bytes.ToArray() : result.Content!, Quality = TagQuality.Good, Timestamp = DateTimeOffset.UtcNow }
        : BadTag(address, dataType, result.Message);

    private static TagValue ToTagValue<TSource>(string address, DataType dataType, OperateResult<TSource> result, Func<TSource, object> projector) => result.IsSuccess
        ? new() { Address = address, DataType = dataType, Value = projector(result.Content), Quality = TagQuality.Good, Timestamp = DateTimeOffset.UtcNow }
        : BadTag(address, dataType, result.Message);

    private static TagValue BadTag(string address, DataType dataType, string? error) => new()
    {
        Address = address,
        DataType = dataType,
        Value = dataType == DataType.String ? string.Empty : dataType == DataType.ByteArray ? Array.Empty<byte>() : dataType == DataType.Bool ? false : 0,
        Quality = TagQuality.Bad,
        Timestamp = DateTimeOffset.UtcNow,
        ErrorMessage = error,
    };
}
