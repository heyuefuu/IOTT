namespace IndustrialIoT.Protocols.FINS;

using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using HslCommunication;
using HslCommunication.Core;
using HslCommunication.Profinet.Omron;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

[ProtocolDriver(ProtocolType.OmronHostLink, "Omron", "欧姆龙", "CJ2M", "CP1W", "CP1H", "CP1E-N")]
public sealed class OmronHostLinkDriver : IProtocolDriver, IAddressSpaceBrowser
{
    private static readonly Regex AddressRegex = new(
        @"^(?<area>DM|CIO|WR|HR|AR|D|C|W|H|A)(?<word>\d+)(?:\.(?<bit>\d+))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const ushort DefaultStringLength = 16;

    private readonly ILogger<OmronHostLinkDriver> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private IHostLinkClient? _client;
    private ConnectionState _state = ConnectionState.Disconnected;

    public OmronHostLinkDriver(ILogger<OmronHostLinkDriver> logger) => _logger = logger;
    public ProtocolType Protocol => ProtocolType.OmronHostLink;
    public ConnectionState State => _state;
    public DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.Write | DriverCapabilities.Browse | DriverCapabilities.BatchRead;
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        if (_state == ConnectionState.Connected) return new() { Success = true };
        SetState(ConnectionState.Connecting);
        try
        {
            var mode = (Get(config, "Mode") ?? (string.IsNullOrWhiteSpace(Get(config, "PortName")) ? "Tcp" : "Serial")).Trim();
            _client?.Dispose();
            _client = mode.Equals("Serial", StringComparison.OrdinalIgnoreCase)
                ? await ConnectSerialAsync(config, ct)
                : await ConnectTcpAsync(config);

            _logger.LogInformation("Omron HostLink connected via {Mode}", mode);
            SetState(ConnectionState.Connected);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            CleanupClient();
            _logger.LogError(ex, "Omron HostLink connection failed");
            SetState(ConnectionState.Faulted, ex.Message);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    private async Task<IHostLinkClient> ConnectTcpAsync(DeviceConnectionConfig config)
    {
        var inner = new OmronHostLinkOverTcp(config.Host, config.Port)
        {
            ConnectTimeOut = (int)config.ConnectTimeout.TotalMilliseconds,
            ReceiveTimeOut = (int)config.ReadTimeout.TotalMilliseconds,
        };
        ApplyCommon(inner, config);
        var result = await inner.ConnectServerAsync();
        if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
        return new TcpHostLinkAdapter(inner);
    }

    private async Task<IHostLinkClient> ConnectSerialAsync(DeviceConnectionConfig config, CancellationToken ct)
    {
        var inner = new OmronHostLink();
        ApplyCommon(inner, config);

        var portName = Get(config, "PortName") ?? throw new InvalidOperationException("Omron HostLink serial mode requires ExtendedProperties['PortName'].");
        var baud = int.TryParse(Get(config, "BaudRate"), out var b) ? b : 9600;
        var dataBits = int.TryParse(Get(config, "DataBits"), out var d) ? d : 7;
        var stopBits = Enum.TryParse<StopBits>(Get(config, "StopBits"), true, out var sb) ? sb : StopBits.Two;
        var parity = Enum.TryParse<Parity>(Get(config, "Parity"), true, out var p) ? p : Parity.Even;
        inner.SerialPortInni(sp => { sp.PortName = portName; sp.BaudRate = baud; sp.DataBits = dataBits; sp.StopBits = stopBits; sp.Parity = parity; });

        var open = await Task.Run(() => inner.Open(), ct);
        if (!open.IsSuccess) throw new InvalidOperationException(open.Message);
        return new SerialHostLinkAdapter(inner);
    }

    private static void ApplyCommon(OmronHostLink inner, DeviceConnectionConfig config)
    {
        if (byte.TryParse(Get(config, "UnitNumber"), out var u)) inner.UnitNumber = u;
        if (byte.TryParse(Get(config, "ICF"), out var icf)) inner.ICF = icf;
        if (byte.TryParse(Get(config, "DA2"), out var da2)) inner.DA2 = da2;
        if (byte.TryParse(Get(config, "SA2"), out var sa2)) inner.SA2 = sa2;
        if (byte.TryParse(Get(config, "SID"), out var sid)) inner.SID = sid;
        if (Enum.TryParse<DataFormat>(Get(config, "DataFormat"), true, out var df)) inner.ByteTransform.DataFormat = df;
        if (bool.TryParse(Get(config, "IsStringReverseByteWord"), out var rev)) inner.ByteTransform.IsStringReverseByteWord = rev;
    }

    private static void ApplyCommon(OmronHostLinkOverTcp inner, DeviceConnectionConfig config)
    {
        if (byte.TryParse(Get(config, "UnitNumber"), out var u)) inner.UnitNumber = u;
        if (byte.TryParse(Get(config, "ICF"), out var icf)) inner.ICF = icf;
        if (byte.TryParse(Get(config, "DA2"), out var da2)) inner.DA2 = da2;
        if (byte.TryParse(Get(config, "SA2"), out var sa2)) inner.SA2 = sa2;
        if (byte.TryParse(Get(config, "SID"), out var sid)) inner.SID = sid;
        if (Enum.TryParse<DataFormat>(Get(config, "DataFormat"), true, out var df)) inner.ByteTransform.DataFormat = df;
        if (bool.TryParse(Get(config, "IsStringReverseByteWord"), out var rev)) inner.ByteTransform.IsStringReverseByteWord = rev;
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_client is not null) { try { await _client.ConnectCloseAsync(); } catch { } }
        CleanupClient();
        SetState(ConnectionState.Disconnected);
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
        var mapped = MapAddress(address, dataType);
        await _semaphore.WaitAsync(ct);
        try
        {
            var c = _client!;
            return dataType switch
            {
                DataType.Bool => ToTagValue(address, dataType, await c.ReadBoolAsync(mapped)),
                DataType.Int16 => ToTagValue(address, dataType, await c.ReadInt16Async(mapped)),
                DataType.UInt16 => ToTagValue(address, dataType, await c.ReadUInt16Async(mapped)),
                DataType.Int32 => ToTagValue(address, dataType, await c.ReadInt32Async(mapped)),
                DataType.UInt32 => ToTagValue(address, dataType, await c.ReadUInt32Async(mapped)),
                DataType.Float => ToTagValue(address, dataType, await c.ReadFloatAsync(mapped)),
                DataType.Int64 => ToTagValue(address, dataType, await c.ReadInt64Async(mapped)),
                DataType.Double => ToTagValue(address, dataType, await c.ReadDoubleAsync(mapped)),
                DataType.String => ToTagValue(address, dataType, await c.ReadStringAsync(mapped, DefaultStringLength, Encoding.UTF8)),
                DataType.ByteArray => ToTagValue(address, dataType, await c.ReadAsync(mapped, 1)),
                _ => ToTagValue(address, dataType, await c.ReadUInt16Async(mapped)),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "HostLink read failed at {Address}", address); return BadTag(address, dataType, ex.Message); }
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
        var mapped = MapAddress(address, dataType);
        await _semaphore.WaitAsync(ct);
        try
        {
            var c = _client!;
            OperateResult result = dataType switch
            {
                DataType.Bool => await c.WriteAsync(mapped, Convert.ToBoolean(value)),
                DataType.Int16 => await c.WriteAsync(mapped, Convert.ToInt16(value)),
                DataType.UInt16 => await c.WriteAsync(mapped, Convert.ToUInt16(value)),
                DataType.Int32 => await c.WriteAsync(mapped, Convert.ToInt32(value)),
                DataType.UInt32 => await c.WriteAsync(mapped, Convert.ToUInt32(value)),
                DataType.Float => await c.WriteAsync(mapped, Convert.ToSingle(value)),
                DataType.Int64 => await c.WriteAsync(mapped, Convert.ToInt64(value)),
                DataType.Double => await c.WriteAsync(mapped, Convert.ToDouble(value)),
                DataType.String => await c.WriteAsync(mapped, Convert.ToString(value) ?? string.Empty, DefaultStringLength, Encoding.UTF8),
                DataType.ByteArray => await c.WriteAsync(mapped, (byte[])value),
                _ => await c.WriteAsync(mapped, Convert.ToUInt16(value)),
            };
            return result.IsSuccess ? new() { Success = true } : new() { Success = false, ErrorMessage = result.Message };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "HostLink write failed at {Address}", address); return new() { Success = false, ErrorMessage = ex.Message }; }
        finally { _semaphore.Release(); }
    }

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        IReadOnlyList<AddressNode> nodes;
        if (string.IsNullOrEmpty(parentPath))
        {
            nodes = FinsProtocol.MemoryAreas.Keys.Select(MakeFolderNode).ToArray();
        }
        else
        {
            var area = parentPath.ToUpperInvariant();
            if (!FinsProtocol.MemoryAreas.TryGetValue(area, out var info))
            {
                nodes = Array.Empty<AddressNode>();
            }
            else
            {
                var list = new List<AddressNode>(info.MaxAddress + 1);
                for (int i = 0; i <= info.MaxAddress; i++) list.Add(MakeVariableNode(area, i));
                nodes = list;
            }
        }
        return Task.FromResult(nodes);
    }

    public Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Path,DisplayName,DataType,Readable,Writable");
        foreach (var (area, info) in FinsProtocol.MemoryAreas)
            for (int i = 0; i <= info.MaxAddress; i++)
                sb.AppendLine($"{area}{i},{area}{i},{DataType.UInt16},True,True");
        Stream stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        return Task.FromResult(stream);
    }

    public async ValueTask DisposeAsync() { await DisconnectAsync(); _semaphore.Dispose(); GC.SuppressFinalize(this); }

    private static string MapAddress(string address, DataType dataType)
    {
        var body = address.Trim();
        var match = AddressRegex.Match(body);
        if (!match.Success) return body;
        string area = match.Groups["area"].Value.ToUpperInvariant();
        int word = int.Parse(match.Groups["word"].Value);
        string prefix = area switch
        {
            "DM" or "D" => "D",
            "CIO" or "C" => "C",
            "WR" or "W" => "W",
            "HR" or "H" => "H",
            "AR" or "A" => "A",
            _ => throw new FormatException($"Unsupported HostLink area '{area}'."),
        };
        if (dataType == DataType.Bool)
        {
            int bit = match.Groups["bit"].Success ? int.Parse(match.Groups["bit"].Value) : 0;
            if (bit is < 0 or > 15) throw new FormatException($"Invalid bit index '{bit}' in '{body}'.");
            return $"{prefix}{word}.{bit}";
        }
        return $"{prefix}{word}";
    }

    private void EnsureConnected() { if (_state != ConnectionState.Connected || _client is null) throw new InvalidOperationException("Omron HostLink driver is not connected"); }
    private void SetState(ConnectionState state, string? reason = null) { var old = _state; if (old == state) return; _state = state; StateChanged?.Invoke(this, new() { OldState = old, NewState = state, Reason = reason }); }
    private void CleanupClient() { try { _client?.Dispose(); } catch { } _client = null; }
    private static string? Get(DeviceConnectionConfig config, string key) => config.ExtendedProperties.TryGetValue(key, out var v) ? v : null;

    private static AddressNode MakeFolderNode(string area) => new() { Path = area, DisplayName = area, NodeType = AddressNodeType.Folder, IsReadable = true, IsWritable = true };
    private static AddressNode MakeVariableNode(string area, int index) => new() { Path = $"{area}{index}", DisplayName = $"{area}{index}", NodeType = AddressNodeType.Variable, DataType = DataType.UInt16, IsReadable = true, IsWritable = true };

    private static TagValue ToTagValue<T>(string address, DataType dataType, OperateResult<T> result) => result.IsSuccess
        ? new() { Address = address, DataType = dataType, Value = result.Content is byte[] bytes ? bytes.ToArray() : result.Content!, Quality = TagQuality.Good, Timestamp = DateTimeOffset.UtcNow }
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

    private interface IHostLinkClient : IDisposable
    {
        Task ConnectCloseAsync();
        Task<OperateResult<byte[]>> ReadAsync(string address, ushort length);
        Task<OperateResult<bool>> ReadBoolAsync(string address);
        Task<OperateResult<short>> ReadInt16Async(string address);
        Task<OperateResult<ushort>> ReadUInt16Async(string address);
        Task<OperateResult<int>> ReadInt32Async(string address);
        Task<OperateResult<uint>> ReadUInt32Async(string address);
        Task<OperateResult<long>> ReadInt64Async(string address);
        Task<OperateResult<float>> ReadFloatAsync(string address);
        Task<OperateResult<double>> ReadDoubleAsync(string address);
        Task<OperateResult<string>> ReadStringAsync(string address, ushort length, Encoding encoding);
        Task<OperateResult> WriteAsync(string address, bool value);
        Task<OperateResult> WriteAsync(string address, short value);
        Task<OperateResult> WriteAsync(string address, ushort value);
        Task<OperateResult> WriteAsync(string address, int value);
        Task<OperateResult> WriteAsync(string address, uint value);
        Task<OperateResult> WriteAsync(string address, long value);
        Task<OperateResult> WriteAsync(string address, float value);
        Task<OperateResult> WriteAsync(string address, double value);
        Task<OperateResult> WriteAsync(string address, byte[] value);
        Task<OperateResult> WriteAsync(string address, string value, int length, Encoding encoding);
    }

    private sealed class TcpHostLinkAdapter : IHostLinkClient
    {
        private readonly OmronHostLinkOverTcp _inner;
        public TcpHostLinkAdapter(OmronHostLinkOverTcp inner) => _inner = inner;
        public Task ConnectCloseAsync() => _inner.ConnectCloseAsync();
        public Task<OperateResult<byte[]>> ReadAsync(string address, ushort length) => _inner.ReadAsync(address, length);
        public Task<OperateResult<bool>> ReadBoolAsync(string address) => _inner.ReadBoolAsync(address);
        public Task<OperateResult<short>> ReadInt16Async(string address) => _inner.ReadInt16Async(address);
        public Task<OperateResult<ushort>> ReadUInt16Async(string address) => _inner.ReadUInt16Async(address);
        public Task<OperateResult<int>> ReadInt32Async(string address) => _inner.ReadInt32Async(address);
        public Task<OperateResult<uint>> ReadUInt32Async(string address) => _inner.ReadUInt32Async(address);
        public Task<OperateResult<long>> ReadInt64Async(string address) => _inner.ReadInt64Async(address);
        public Task<OperateResult<float>> ReadFloatAsync(string address) => _inner.ReadFloatAsync(address);
        public Task<OperateResult<double>> ReadDoubleAsync(string address) => _inner.ReadDoubleAsync(address);
        public Task<OperateResult<string>> ReadStringAsync(string address, ushort length, Encoding encoding) => _inner.ReadStringAsync(address, length, encoding);
        public Task<OperateResult> WriteAsync(string address, bool value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, short value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, ushort value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, int value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, uint value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, long value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, float value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, double value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, byte[] value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, string value, int length, Encoding encoding) => _inner.WriteAsync(address, value, length, encoding);
        public void Dispose() => _inner.Dispose();
    }

    private sealed class SerialHostLinkAdapter : IHostLinkClient
    {
        private readonly OmronHostLink _inner;
        public SerialHostLinkAdapter(OmronHostLink inner) => _inner = inner;
        public Task ConnectCloseAsync() { _inner.Close(); return Task.CompletedTask; }
        public Task<OperateResult<byte[]>> ReadAsync(string address, ushort length) => _inner.ReadAsync(address, length);
        public Task<OperateResult<bool>> ReadBoolAsync(string address) => _inner.ReadBoolAsync(address);
        public Task<OperateResult<short>> ReadInt16Async(string address) => _inner.ReadInt16Async(address);
        public Task<OperateResult<ushort>> ReadUInt16Async(string address) => _inner.ReadUInt16Async(address);
        public Task<OperateResult<int>> ReadInt32Async(string address) => _inner.ReadInt32Async(address);
        public Task<OperateResult<uint>> ReadUInt32Async(string address) => _inner.ReadUInt32Async(address);
        public Task<OperateResult<long>> ReadInt64Async(string address) => _inner.ReadInt64Async(address);
        public Task<OperateResult<float>> ReadFloatAsync(string address) => _inner.ReadFloatAsync(address);
        public Task<OperateResult<double>> ReadDoubleAsync(string address) => _inner.ReadDoubleAsync(address);
        public Task<OperateResult<string>> ReadStringAsync(string address, ushort length, Encoding encoding) => _inner.ReadStringAsync(address, length, encoding);
        public Task<OperateResult> WriteAsync(string address, bool value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, short value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, ushort value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, int value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, uint value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, long value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, float value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, double value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, byte[] value) => _inner.WriteAsync(address, value);
        public Task<OperateResult> WriteAsync(string address, string value, int length, Encoding encoding) => _inner.WriteAsync(address, value, length, encoding);
        public void Dispose() => _inner.Dispose();
    }
}
