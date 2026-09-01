namespace IndustrialIoT.Protocols.Mewtocol;

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

[ProtocolDriver(ProtocolType.Mewtocol, "Panasonic", "松下", "FP")]
public class MewtocolDriver : IProtocolDriver, IAddressSpaceBrowser
{
    private static readonly Regex NumericAddressRegex = new(
        @"^(?<prefix>DT|SR|X|Y|R|T|C)(?<number>\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NativeAddressRegex = new(
        @"^(?<prefix>SR|X|Y|R|T|C|D|LD|L|F)\d+(?:\.[0-9A-F]+|[A-F])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const ushort DefaultStringLength = 16;

    private readonly ILogger<MewtocolDriver> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private PanasonicMewtocolOverTcp? _client;
    private ConnectionState _state = ConnectionState.Disconnected;
    private byte _station = 238;

    public ProtocolType Protocol => ProtocolType.Mewtocol;
    public ConnectionState State => _state;

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Read | DriverCapabilities.Write |
        DriverCapabilities.Browse | DriverCapabilities.BatchRead;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public MewtocolDriver(ILogger<MewtocolDriver> logger)
    {
        _logger = logger;
    }

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        if (_state == ConnectionState.Connected)
            return new() { Success = true };

        SetState(ConnectionState.Connecting);
        _station = ParseStation(config);

        try
        {
            var client = new PanasonicMewtocolOverTcp(config.Host, config.Port, _station)
            {
                ConnectTimeOut = (int)config.ConnectTimeout.TotalMilliseconds,
                ReceiveTimeOut = (int)config.ReadTimeout.TotalMilliseconds,
                Station = _station,
            };
            ApplyExtendedProperties(client, config);

            var result = await client.ConnectServerAsync();
            if (!result.IsSuccess)
                throw new InvalidOperationException(result.Message);

            _client?.Dispose();
            _client = client;

            _logger.LogInformation(
                "Mewtocol connected via HslCommunication to {Host}:{Port} station {Station}",
                config.Host, config.Port, _station);

            SetState(ConnectionState.Connected);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            CleanupClient();
            _logger.LogError(ex, "Mewtocol connection failed: {Host}:{Port}", config.Host, config.Port);
            SetState(ConnectionState.Faulted, ex.Message);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_client is not null)
        {
            try { await _client.ConnectCloseAsync(); } catch { }
        }

        CleanupClient();
        SetState(ConnectionState.Disconnected);
        _logger.LogInformation("Mewtocol disconnected.");
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_state != ConnectionState.Connected || _client is null)
            return false;

        await _semaphore.WaitAsync(ct);
        try
        {
            var result = await GetReadWriteNet().ReadUInt16Async("D0");
            return result.IsSuccess;
        }
        catch
        {
            return false;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        EnsureConnected();
        string mappedAddress = MapAddress(address, dataType);

        await _semaphore.WaitAsync(ct);
        try
        {
            var client = GetReadWriteNet();
            var body = GetAddressBody(mappedAddress);

            if (ShouldUseBoolAccess(body, dataType))
            {
                return ToTagValue(address, dataType, await client.ReadBoolAsync(mappedAddress));
            }

            if (IsTimerOrCounterAddress(body))
            {
                var result = await client.ReadUInt16Async(mappedAddress);
                if (!result.IsSuccess)
                    return BadTag(address, dataType, result.Message);

                object timerOrCounterValue = dataType == DataType.Bool ? result.Content > 0 : result.Content;
                return new()
                {
                    Address = address,
                    DataType = dataType,
                    Value = timerOrCounterValue,
                    Quality = TagQuality.Good,
                    Timestamp = DateTimeOffset.UtcNow,
                };
            }

            return dataType switch
            {
                DataType.Bool => ToTagValue(address, dataType, await client.ReadUInt16Async(mappedAddress), v => v > 0),
                DataType.Int16 => ToTagValue(address, dataType, await client.ReadInt16Async(mappedAddress)),
                DataType.UInt16 => ToTagValue(address, dataType, await client.ReadUInt16Async(mappedAddress)),
                DataType.Int32 => ToTagValue(address, dataType, await client.ReadInt32Async(mappedAddress)),
                DataType.UInt32 => ToTagValue(address, dataType, await client.ReadUInt32Async(mappedAddress)),
                DataType.Float => ToTagValue(address, dataType, await client.ReadFloatAsync(mappedAddress)),
                DataType.Int64 => ToTagValue(address, dataType, await client.ReadInt64Async(mappedAddress)),
                DataType.Double => ToTagValue(address, dataType, await client.ReadDoubleAsync(mappedAddress)),
                DataType.String => ToTagValue(address, dataType, await client.ReadStringAsync(mappedAddress, DefaultStringLength, Encoding.ASCII)),
                DataType.ByteArray => ToTagValue(address, dataType, await client.ReadAsync(mappedAddress, GetWordCount(dataType))),
                _ => ToTagValue(address, dataType, await client.ReadUInt16Async(mappedAddress)),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Read failed for {Address}", address);
            return BadTag(address, dataType, ex.Message);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(
        IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        var results = new List<TagValue>(requests.Count);
        foreach (var req in requests)
            results.Add(await ReadTagAsync(req.Address, req.DataType, ct));
        return results;
    }

    public async Task<WriteResult> WriteTagAsync(
        string address, DataType dataType, object value, CancellationToken ct = default)
    {
        EnsureConnected();
        string mappedAddress = MapAddress(address, dataType);

        await _semaphore.WaitAsync(ct);
        try
        {
            var client = GetReadWriteNet();
            var body = GetAddressBody(mappedAddress);
            OperateResult result;

            if (ShouldUseBoolAccess(body, dataType))
            {
                result = await client.WriteAsync(mappedAddress, Convert.ToBoolean(value));
            }
            else if (IsTimerOrCounterAddress(body))
            {
                result = await client.WriteAsync(mappedAddress, Convert.ToUInt16(value));
            }
            else
            {
                result = dataType switch
                {
                    DataType.Bool => await client.WriteAsync(mappedAddress, Convert.ToUInt16(Convert.ToBoolean(value) ? 1 : 0)),
                    DataType.Int16 => await client.WriteAsync(mappedAddress, Convert.ToInt16(value)),
                    DataType.UInt16 => await client.WriteAsync(mappedAddress, Convert.ToUInt16(value)),
                    DataType.Int32 => await client.WriteAsync(mappedAddress, Convert.ToInt32(value)),
                    DataType.UInt32 => await client.WriteAsync(mappedAddress, Convert.ToUInt32(value)),
                    DataType.Float => await client.WriteAsync(mappedAddress, Convert.ToSingle(value)),
                    DataType.Int64 => await client.WriteAsync(mappedAddress, Convert.ToInt64(value)),
                    DataType.Double => await client.WriteAsync(mappedAddress, Convert.ToDouble(value)),
                    DataType.String => await client.WriteAsync(mappedAddress, Convert.ToString(value) ?? string.Empty, DefaultStringLength, Encoding.ASCII),
                    DataType.ByteArray => await client.WriteAsync(mappedAddress, (byte[])value),
                    _ => await client.WriteAsync(mappedAddress, Convert.ToUInt16(value)),
                };
            }

            if (!result.IsSuccess)
            {
                _logger.LogWarning("Mewtocol write error at {Address}: {Error}", address, result.Message);
                return new() { Success = false, ErrorMessage = result.Message };
            }

            return new() { Success = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Write failed for {Address}", address);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(
        string? parentPath = null, CancellationToken ct = default)
    {
        IReadOnlyList<AddressNode> nodes = parentPath?.ToUpperInvariant() switch
        {
            null or "" => BuildRootNodes(),
            "D"  => BuildRegisterNodes("D",  "数据寄存器",   0, 32767, DataType.Int16, true),
            "LD" => BuildRegisterNodes("LD", "链接寄存器",   0, 32767, DataType.Int16, true),
            "L"  => BuildRegisterNodes("L",  "链接继电器",   0, 511,   DataType.Bool,  true),
            "F"  => BuildRegisterNodes("F",  "文件寄存器",   0, 1023,  DataType.UInt16, true),
            "DT" => BuildRegisterNodes("DT", "数据寄存器", 0, 32767, DataType.Int16, true),
            "SR" => BuildRegisterNodes("SR", "特殊寄存器", 0, 1023, DataType.UInt16, false),
            "X"  => BuildRegisterNodes("X", "外部输入", 0, 127, DataType.Bool, false),
            "Y"  => BuildRegisterNodes("Y", "外部输出", 0, 127, DataType.Bool, true),
            "R"  => BuildRegisterNodes("R", "内部继电器", 0, 511, DataType.Bool, true),
            "T"  => BuildRegisterNodes("T", "定时器", 0, 99, DataType.UInt16, true),
            "C"  => BuildRegisterNodes("C", "计数器", 0, 99, DataType.UInt16, true),
            _ => []
        };
        return Task.FromResult(nodes);
    }

    public Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Path,DisplayName,DataType,Readable,Writable");

        ExportRegisterRange(sb, "D",  "数据寄存器", 0, 32767, DataType.Int16, true);
        ExportRegisterRange(sb, "LD", "链接寄存器", 0, 32767, DataType.Int16, true);
        ExportRegisterRange(sb, "L",  "链接继电器", 0, 511, DataType.Bool, true);
        ExportRegisterRange(sb, "F",  "文件寄存器", 0, 1023, DataType.UInt16, true);
        ExportRegisterRange(sb, "DT", "数据寄存器", 0, 32767, DataType.Int16, true);
        ExportRegisterRange(sb, "SR", "特殊寄存器", 0, 1023, DataType.UInt16, false);
        ExportRegisterRange(sb, "X", "外部输入", 0, 127, DataType.Bool, false);
        ExportRegisterRange(sb, "Y", "外部输出", 0, 127, DataType.Bool, true);
        ExportRegisterRange(sb, "R", "内部继电器", 0, 511, DataType.Bool, true);
        ExportRegisterRange(sb, "T", "定时器", 0, 99, DataType.UInt16, true);
        ExportRegisterRange(sb, "C", "计数器", 0, 99, DataType.UInt16, true);

        Stream stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        return Task.FromResult(stream);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    private static byte ParseStation(DeviceConnectionConfig config)
    {
        if (config.ExtendedProperties.TryGetValue("Station", out var station)
            && byte.TryParse(station, out var parsed))
        {
            return parsed;
        }

        return 238;
    }

    private static void ApplyExtendedProperties(PanasonicMewtocolOverTcp client, DeviceConnectionConfig config)
    {
        if (config.ExtendedProperties.TryGetValue("DataFormat", out var dataFormat)
            && Enum.TryParse<DataFormat>(dataFormat, true, out var parsedDataFormat))
        {
            client.ByteTransform.DataFormat = parsedDataFormat;
        }

        if (config.ExtendedProperties.TryGetValue("IsStringReverseByteWord", out var reverse)
            && bool.TryParse(reverse, out var parsedReverse))
        {
            client.ByteTransform.IsStringReverseByteWord = parsedReverse;
        }
    }

    private IReadWriteNet GetReadWriteNet() => _client ?? throw new InvalidOperationException("Driver is not connected.");

    private static string MapAddress(string address, DataType dataType)
    {
        var normalized = address.Trim();
        var stationPrefix = string.Empty;
        var body = normalized;

        var separatorIndex = normalized.IndexOf(';');
        if (normalized.StartsWith("s=", StringComparison.OrdinalIgnoreCase) && separatorIndex > 2)
        {
            stationPrefix = normalized[..(separatorIndex + 1)];
            body = normalized[(separatorIndex + 1)..];
        }

        var upperBody = body.ToUpperInvariant();
        var numericMatch = NumericAddressRegex.Match(upperBody);
        if (numericMatch.Success)
        {
            var prefix = numericMatch.Groups["prefix"].Value.ToUpperInvariant();
            var number = numericMatch.Groups["number"].Value;
            return stationPrefix + (prefix == "DT" ? $"D{number}" : $"{prefix}{number}");
        }

        if (NativeAddressRegex.IsMatch(upperBody))
            return stationPrefix + upperBody;

        throw new MewtocolException($"Invalid Mewtocol address: '{address}'. Expected HSL-compatible Panasonic address.");
    }

    private static string GetAddressBody(string address)
    {
        var separatorIndex = address.IndexOf(';');
        return separatorIndex >= 0 ? address[(separatorIndex + 1)..].ToUpperInvariant() : address.ToUpperInvariant();
    }

    private static bool ShouldUseBoolAccess(string body, DataType dataType)
    {
        if (dataType != DataType.Bool)
            return false;

        return body.StartsWith("X", StringComparison.Ordinal)
            || body.StartsWith("Y", StringComparison.Ordinal)
            || body.StartsWith("R", StringComparison.Ordinal)
            || body.StartsWith("L", StringComparison.Ordinal)
            || body.StartsWith("LD", StringComparison.Ordinal)
            || body.StartsWith("D", StringComparison.Ordinal)
            || body.StartsWith("F", StringComparison.Ordinal);
    }

    private static bool IsTimerOrCounterAddress(string body) =>
        body.StartsWith("T", StringComparison.Ordinal) || body.StartsWith("C", StringComparison.Ordinal);

    private static ushort GetWordCount(DataType dataType) => dataType switch
    {
        DataType.Bool => 1,
        DataType.Int16 => 1,
        DataType.UInt16 => 1,
        DataType.Int32 => 2,
        DataType.UInt32 => 2,
        DataType.Float => 2,
        DataType.Int64 => 4,
        DataType.Double => 4,
        DataType.String => 8,
        _ => 1,
    };

    private void SetState(ConnectionState newState, string? reason = null)
    {
        var old = _state;
        if (old == newState) return;
        _state = newState;
        StateChanged?.Invoke(this, new() { OldState = old, NewState = newState, Reason = reason });
    }

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected || _client is null)
            throw new InvalidOperationException($"Driver is not connected (state={_state}).");
    }

    private void CleanupClient()
    {
        try { _client?.Dispose(); } catch { }
        _client = null;
    }

    private static TagValue ToTagValue<T>(string address, DataType dataType, OperateResult<T> result)
    {
        if (!result.IsSuccess)
            return BadTag(address, dataType, result.Message);

        object value = result.Content is byte[] bytes ? bytes.ToArray() : result.Content!;
        return new()
        {
            Address = address,
            DataType = dataType,
            Value = value,
            Quality = TagQuality.Good,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    private static TagValue ToTagValue<TSource>(
        string address,
        DataType dataType,
        OperateResult<TSource> result,
        Func<TSource, object> projector)
    {
        if (!result.IsSuccess)
            return BadTag(address, dataType, result.Message);

        return new()
        {
            Address = address,
            DataType = dataType,
            Value = projector(result.Content),
            Quality = TagQuality.Good,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    private static TagValue BadTag(string address, DataType dataType, string? error) => new()
    {
        Address = address,
        DataType = dataType,
        Value = dataType switch
        {
            DataType.Bool => false,
            DataType.Int16 => (short)0,
            DataType.UInt16 => (ushort)0,
            DataType.Int32 => 0,
            DataType.UInt32 => 0u,
            DataType.Float => 0f,
            DataType.Int64 => 0L,
            DataType.Double => 0d,
            DataType.String => string.Empty,
            DataType.ByteArray => Array.Empty<byte>(),
            _ => (short)0,
        },
        Quality = TagQuality.Bad,
        Timestamp = DateTimeOffset.UtcNow,
        ErrorMessage = error,
    };

    private static void ExportRegisterRange(
        StringBuilder sb, string prefix, string displayPrefix,
        int min, int max, DataType dataType, bool writable)
    {
        for (int i = min; i <= max; i++)
        {
            sb.AppendLine($"{prefix}{i},{prefix}{i} {displayPrefix},{dataType},True,{writable}");
        }
    }

    private static IReadOnlyList<AddressNode> BuildRootNodes() =>
    [
        new() { Path = "D",  DisplayName = "D 数据寄存器 (0-32767)",   NodeType = AddressNodeType.Folder, IsReadable = true, IsWritable = true },
        new() { Path = "LD", DisplayName = "LD 链接寄存器 (0-32767)", NodeType = AddressNodeType.Folder, IsReadable = true, IsWritable = true },
        new() { Path = "L",  DisplayName = "L 链接继电器 (0-511)",    NodeType = AddressNodeType.Folder, IsReadable = true, IsWritable = true },
        new() { Path = "F",  DisplayName = "F 文件寄存器 (0-1023)",   NodeType = AddressNodeType.Folder, IsReadable = true, IsWritable = true },
        new() { Path = "DT", DisplayName = "DT 数据寄存器 (0-32767)", NodeType = AddressNodeType.Folder, IsReadable = true, IsWritable = true },
        new() { Path = "SR", DisplayName = "SR 特殊寄存器 (0-1023)", NodeType = AddressNodeType.Folder, IsReadable = true, IsWritable = false },
        new() { Path = "X",  DisplayName = "X 外部输入 (0-127)",     NodeType = AddressNodeType.Folder, IsReadable = true, IsWritable = false },
        new() { Path = "Y",  DisplayName = "Y 外部输出 (0-127)",     NodeType = AddressNodeType.Folder, IsReadable = true, IsWritable = true },
        new() { Path = "R",  DisplayName = "R 内部继电器 (0-511)",   NodeType = AddressNodeType.Folder, IsReadable = true, IsWritable = true },
        new() { Path = "T",  DisplayName = "T 定时器 (0-99)",        NodeType = AddressNodeType.Folder, IsReadable = true, IsWritable = true },
        new() { Path = "C",  DisplayName = "C 计数器 (0-99)",        NodeType = AddressNodeType.Folder, IsReadable = true, IsWritable = true },
    ];

    private static IReadOnlyList<AddressNode> BuildRegisterNodes(
        string prefix, string displayPrefix, int min, int max, DataType dataType, bool writable)
    {
        var nodes = new List<AddressNode>(max - min + 1);
        for (int i = min; i <= max; i++)
        {
            nodes.Add(new()
            {
                Path = $"{prefix}{i}",
                DisplayName = $"{prefix}{i} {displayPrefix}",
                NodeType = AddressNodeType.Variable,
                DataType = dataType,
                IsReadable = true,
                IsWritable = writable
            });
        }
        return nodes;
    }
}
