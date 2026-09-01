namespace IndustrialIoT.Protocols.SiemensS7;

using System.Text;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

[ProtocolDriver(ProtocolType.SiemensS7, "Siemens", "西门子", "S7-1200", "S7-1500", "S7-300", "S7-400", "S7-200Smart")]
public sealed class SiemensS7Driver(ILogger<SiemensS7Driver> logger) : IProtocolDriver, IAddressSpaceBrowser
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private S7TcpClient? _client;
    private ConnectionState _state = ConnectionState.Disconnected;
    private ushort _stringLength = 16;

    public ProtocolType Protocol => ProtocolType.SiemensS7;
    public ConnectionState State => _state;
    public DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.Write | DriverCapabilities.Browse | DriverCapabilities.BatchRead;
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            if (_state == ConnectionState.Connected) return new() { Success = true };
            SetState(ConnectionState.Connecting);
            var client = new S7TcpClient();
            await client.ConnectAsync(config.Host, config.Port, GetPlcType(config), GetByte(config, "Rack", 0), GetByte(config, "Slot", GetDefaultSlot(config)), config.ConnectTimeout, ct);
            _stringLength = GetUShort(config, "StringLength", 16);
            _client = client;
            SetState(ConnectionState.Connected);
            return new() { Success = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await CleanupAsync();
            logger.LogError(ex, "Siemens S7 connection failed.");
            SetState(ConnectionState.Faulted, ex.Message);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            await CleanupAsync();
            SetState(ConnectionState.Disconnected);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_state != ConnectionState.Connected || _client is null) return false;
        await _semaphore.WaitAsync(ct);
        try { return await _client.PingAsync(ct); }
        catch { return false; }
        finally { _semaphore.Release(); }
    }

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            var s7Address = S7Address.Parse(address, S7ValueCodec.GetLength(dataType, _stringLength));
            var result = await _client!.ReadAsync(s7Address, ct);
            return result.Success
                ? GoodTag(address, dataType, S7ValueCodec.FromBytes(result.Value!, dataType))
                : BadTag(address, dataType, result.ErrorMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Siemens S7 read failed at {Address}", address);
            return BadTag(address, dataType, ex.Message);
        }
        finally { _semaphore.Release(); }
    }

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        var values = new List<TagValue>(requests.Count);
        foreach (var request in requests)
            values.Add(await ReadTagAsync(request.Address, request.DataType, ct));
        return values;
    }

    public async Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            var s7Address = S7Address.Parse(address, S7ValueCodec.GetLength(dataType, _stringLength));
            var bytes = S7ValueCodec.GetBytes(dataType, value, _stringLength);
            var result = await _client!.WriteAsync(s7Address, bytes, ct);
            return result.Success ? new() { Success = true } : new() { Success = false, ErrorMessage = result.ErrorMessage };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Siemens S7 write failed at {Address}", address);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
        finally { _semaphore.Release(); }
    }

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        IReadOnlyList<AddressNode> nodes = string.IsNullOrWhiteSpace(parentPath)
            ? [Folder("I", "输入区"), Folder("Q", "输出区"), Folder("M", "标志位区"), Folder("DB1", "数据块 DB1")]
            : BuildAreaNodes(parentPath);
        return Task.FromResult(nodes);
    }

    public Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var lines = new List<string> { "Path,DisplayName,DataType,Readable,Writable" };
        foreach (var area in new[] { "I", "Q", "M", "DB1" })
            lines.AddRange(BuildAreaNodes(area).Select(x => $"{x.Path},{x.DisplayName},{x.DataType},True,{x.IsWritable}"));
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, lines))));
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected || _client is null)
            throw new InvalidOperationException("Siemens S7 driver is not connected.");
    }

    private async Task CleanupAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        _client = null;
    }

    private void SetState(ConnectionState state, string? reason = null)
    {
        var old = _state;
        if (old == state) return;
        _state = state;
        StateChanged?.Invoke(this, new() { OldState = old, NewState = state, Reason = reason });
    }

    private static SiemensS7PlcType GetPlcType(DeviceConnectionConfig config)
    {
        var raw = Get(config, "PlcType", "CpuType", "Series", "Model") ?? "S1200";
        var normalized = raw.Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<SiemensS7PlcType>(normalized, true, out var plcType) ? plcType : SiemensS7PlcType.S1200;
    }

    private static byte GetDefaultSlot(DeviceConnectionConfig config) => GetPlcType(config) switch
    {
        SiemensS7PlcType.S300 => 2,
        SiemensS7PlcType.S400 => 3,
        _ => 0,
    };

    private static string? Get(DeviceConnectionConfig config, params string[] keys)
    {
        foreach (var key in keys)
            if (config.ExtendedProperties.TryGetValue(key, out var value))
                return value;
        return null;
    }

    private static byte GetByte(DeviceConnectionConfig config, string key, byte fallback) =>
        config.ExtendedProperties.TryGetValue(key, out var raw) && byte.TryParse(raw, out var value) ? value : fallback;

    private static ushort GetUShort(DeviceConnectionConfig config, string key, ushort fallback) =>
        config.ExtendedProperties.TryGetValue(key, out var raw) && ushort.TryParse(raw, out var value) ? value : fallback;

    private static AddressNode Folder(string path, string displayName) => new()
    {
        Path = path,
        DisplayName = displayName,
        NodeType = AddressNodeType.Folder,
        IsReadable = false,
        IsWritable = false,
    };

    private static IReadOnlyList<AddressNode> BuildAreaNodes(string parentPath)
    {
        var prefix = parentPath.Trim().ToUpperInvariant();
        if (prefix is not ("I" or "Q" or "M" or "DB1")) return [];
        return Enumerable.Range(0, 64).Select(i => new AddressNode
        {
            Path = prefix == "DB1" ? $"DB1.{i}" : $"{prefix}{i}",
            DisplayName = prefix == "DB1" ? $"DB1.{i}" : $"{prefix}{i}",
            NodeType = AddressNodeType.Variable,
            DataType = DataType.Int16,
            IsReadable = true,
            IsWritable = prefix is "Q" or "M" or "DB1",
        }).ToArray();
    }

    private static TagValue GoodTag(string address, DataType dataType, object value) => new()
    {
        Address = address,
        DataType = dataType,
        Value = value,
        Quality = TagQuality.Good,
        Timestamp = DateTimeOffset.UtcNow,
    };

    private static TagValue BadTag(string address, DataType dataType, string? error) => new()
    {
        Address = address,
        DataType = dataType,
        Value = dataType switch { DataType.Bool => false, DataType.String => string.Empty, DataType.ByteArray => Array.Empty<byte>(), _ => 0 },
        Quality = TagQuality.Bad,
        Timestamp = DateTimeOffset.UtcNow,
        ErrorMessage = error,
    };
}
