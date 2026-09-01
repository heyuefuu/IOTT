namespace IndustrialIoT.Protocols.FINS;

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

[ProtocolDriver(ProtocolType.FINS, "Omron", "欧姆龙", "CJ2M", "CP1W", "CP1H", "CP1E-N")]
public sealed class FinsDriver : IProtocolDriver, IAddressSpaceBrowser
{
    private static readonly Regex AddressRegex = new(
        @"^(?<area>DM|CIO|WR|HR|AR)(?<word>\d+)(?:\.(?<bit>\d+))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const ushort DefaultStringLength = 16;

    private readonly ILogger<FinsDriver> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private OmronFinsNet? _client;
    private ConnectionState _state = ConnectionState.Disconnected;

    public ProtocolType Protocol => ProtocolType.FINS;
    public ConnectionState State => _state;

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Read | DriverCapabilities.Write |
        DriverCapabilities.Browse | DriverCapabilities.BatchRead;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public FinsDriver(ILogger<FinsDriver> logger)
    {
        _logger = logger;
    }

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        if (_state == ConnectionState.Connected)
            return new() { Success = true };

        SetState(ConnectionState.Connecting);

        try
        {
            var client = new OmronFinsNet(config.Host, config.Port)
            {
                ConnectTimeOut = (int)config.ConnectTimeout.TotalMilliseconds,
                ReceiveTimeOut = (int)config.ReadTimeout.TotalMilliseconds,
            };
            ApplyExtendedProperties(client, config);

            var result = await client.ConnectServerAsync();
            if (!result.IsSuccess)
                throw new InvalidOperationException(result.Message);

            _client?.Dispose();
            _client = client;

            _logger.LogInformation(
                "FINS connected via HslCommunication to {Host}:{Port}",
                config.Host, config.Port);

            SetState(ConnectionState.Connected);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            CleanupClient();
            _logger.LogError(ex, "FINS connection failed");
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
            return dataType switch
            {
                DataType.Bool => ToTagValue(address, dataType, await client.ReadBoolAsync(mappedAddress)),
                DataType.Int16 => ToTagValue(address, dataType, await client.ReadInt16Async(mappedAddress)),
                DataType.UInt16 => ToTagValue(address, dataType, await client.ReadUInt16Async(mappedAddress)),
                DataType.Int32 => ToTagValue(address, dataType, await client.ReadInt32Async(mappedAddress)),
                DataType.UInt32 => ToTagValue(address, dataType, await client.ReadUInt32Async(mappedAddress)),
                DataType.Float => ToTagValue(address, dataType, await client.ReadFloatAsync(mappedAddress)),
                DataType.Int64 => ToTagValue(address, dataType, await client.ReadInt64Async(mappedAddress)),
                DataType.Double => ToTagValue(address, dataType, await client.ReadDoubleAsync(mappedAddress)),
                DataType.String => ToTagValue(address, dataType, await client.ReadStringAsync(mappedAddress, DefaultStringLength, Encoding.UTF8)),
                DataType.ByteArray => ToTagValue(address, dataType, await client.ReadAsync(mappedAddress, GetWordCount(dataType))),
                _ => ToTagValue(address, dataType, await client.ReadUInt16Async(mappedAddress)),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "FINS read failed at {Address}", address);
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
            OperateResult result = dataType switch
            {
                DataType.Bool => await client.WriteAsync(mappedAddress, Convert.ToBoolean(value)),
                DataType.Int16 => await client.WriteAsync(mappedAddress, Convert.ToInt16(value)),
                DataType.UInt16 => await client.WriteAsync(mappedAddress, Convert.ToUInt16(value)),
                DataType.Int32 => await client.WriteAsync(mappedAddress, Convert.ToInt32(value)),
                DataType.UInt32 => await client.WriteAsync(mappedAddress, Convert.ToUInt32(value)),
                DataType.Float => await client.WriteAsync(mappedAddress, Convert.ToSingle(value)),
                DataType.Int64 => await client.WriteAsync(mappedAddress, Convert.ToInt64(value)),
                DataType.Double => await client.WriteAsync(mappedAddress, Convert.ToDouble(value)),
                DataType.String => await client.WriteAsync(mappedAddress, Convert.ToString(value) ?? string.Empty, DefaultStringLength, Encoding.UTF8),
                DataType.ByteArray => await client.WriteAsync(mappedAddress, (byte[])value),
                _ => await client.WriteAsync(mappedAddress, Convert.ToUInt16(value)),
            };

            if (!result.IsSuccess)
            {
                _logger.LogWarning("FINS write error at {Address}: {Error}", address, result.Message);
                return new() { Success = false, ErrorMessage = result.Message };
            }

            return new() { Success = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "FINS write failed at {Address}", address);
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
        IReadOnlyList<AddressNode> nodes;

        if (string.IsNullOrEmpty(parentPath))
        {
            nodes = GetRootNodes();
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
                for (int i = 0; i <= info.MaxAddress; i++)
                {
                    list.Add(MakeAddressNode(area, i));
                }
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
        {
            for (int i = 0; i <= info.MaxAddress; i++)
            {
                sb.AppendLine($"{area}{i},{area}{i},{DataType.UInt16},True,True");
            }
        }

        Stream stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        return Task.FromResult(stream);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void ApplyExtendedProperties(OmronFinsNet client, DeviceConnectionConfig config)
    {
        if (TryGetByte(config, out var sourceNode, "NodeAddress", "SA1")) client.SA1 = sourceNode;
        if (TryGetByte(config, out var sourceNetwork, "SourceNetwork", "SNA")) client.SNA = sourceNetwork;
        if (TryGetByte(config, out var sourceUnit, "SourceUnit", "SA2")) client.SA2 = sourceUnit;
        if (TryGetByte(config, out var destNetwork, "DestNetwork", "DNA")) client.DNA = destNetwork;
        if (TryGetByte(config, out var destNode, "DestNode", "DA1")) client.DA1 = destNode;
        if (TryGetByte(config, out var destUnit, "DestUnit", "DA2")) client.DA2 = destUnit;
        if (TryGetEnum(config, out OmronPlcType plcType, "PlcType")) client.PlcType = plcType;
        if (TryGetEnum(config, out DataFormat dataFormat, "DataFormat")) client.ByteTransform.DataFormat = dataFormat;
        if (TryGetBool(config, out var reverse, "IsStringReverseByteWord", "StringReverse")) client.ByteTransform.IsStringReverseByteWord = reverse;
        if (TryGetBool(config, out var receiveUntilEmpty, "ReceiveUntilEmpty")) client.ReceiveUntilEmpty = receiveUntilEmpty;
    }

    private IReadWriteNet GetReadWriteNet() => _client ?? throw new InvalidOperationException("FINS driver is not connected");

    private static bool TryGetByte(DeviceConnectionConfig config, out byte value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (config.ExtendedProperties.TryGetValue(key, out var raw) && byte.TryParse(raw, out value))
                return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetBool(DeviceConnectionConfig config, out bool value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (config.ExtendedProperties.TryGetValue(key, out var raw) && bool.TryParse(raw, out value))
                return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetEnum<TEnum>(DeviceConnectionConfig config, out TEnum value, params string[] keys)
        where TEnum : struct
    {
        foreach (var key in keys)
        {
            if (config.ExtendedProperties.TryGetValue(key, out var raw)
                && Enum.TryParse<TEnum>(raw, true, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected || _client is null)
            throw new InvalidOperationException("FINS driver is not connected");
    }

    private void SetState(ConnectionState newState, string? reason = null)
    {
        var old = _state;
        if (old == newState) return;
        _state = newState;
        StateChanged?.Invoke(this, new() { OldState = old, NewState = newState, Reason = reason });
    }

    private void CleanupClient()
    {
        try { _client?.Dispose(); } catch { }
        _client = null;
    }

    private static string MapAddress(string address, DataType dataType)
    {
        var normalized = address.Trim();
        var match = AddressRegex.Match(normalized);
        if (!match.Success)
            return normalized;

        string area = match.Groups["area"].Value.ToUpperInvariant();
        int word = int.Parse(match.Groups["word"].Value);
        string prefix = area switch
        {
            "DM" => "D",
            "CIO" => "C",
            "WR" => "W",
            "HR" => "H",
            "AR" => "A",
            _ => throw new FormatException($"Unsupported FINS area '{area}'.")
        };

        if (dataType == DataType.Bool)
        {
            int bit = match.Groups["bit"].Success ? int.Parse(match.Groups["bit"].Value) : 0;
            if (bit is < 0 or > 15)
                throw new FormatException($"Invalid FINS bit index '{bit}' in address '{normalized}'.");
            return $"{prefix}{word}.{bit}";
        }

        return $"{prefix}{word}";
    }

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

    private static TagValue BadTag(string address, DataType dataType, string? error) => new()
    {
        Address = address,
        DataType = dataType,
        Value = GetDefaultValue(dataType),
        Quality = TagQuality.Bad,
        Timestamp = DateTimeOffset.UtcNow,
        ErrorMessage = error,
    };

    private static object GetDefaultValue(DataType dataType) => dataType switch
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
        _ => 0,
    };

    private static AddressNode MakeAddressNode(string area, int address) => new()
    {
        Path = $"{area}{address}",
        DisplayName = $"{area}{address}",
        NodeType = AddressNodeType.Variable,
        DataType = DataType.UInt16,
        IsReadable = true,
        IsWritable = true,
    };

    private static IReadOnlyList<AddressNode> GetRootNodes() =>
        FinsProtocol.MemoryAreas.Keys.Select(MakeFolderNode).ToArray();

    private static AddressNode MakeFolderNode(string area) => new()
    {
        Path = area,
        DisplayName = GetAreaDisplayName(area),
        NodeType = AddressNodeType.Folder,
        IsReadable = true,
        IsWritable = true,
    };

    private static string GetAreaDisplayName(string area) => area switch
    {
        "CIO" => "CIO - I/O区 (0-6143)",
        "DM" => "DM - 数据存储区 (0-32767)",
        "WR" => "WR - 内部辅助区 (0-511)",
        "HR" => "HR - 保持区 (0-511)",
        "AR" => "AR - 辅助区 (0-959)",
        "E" => "E - EM扩展存储区",
        "TIM" => "TIM - 定时器",
        "CNT" => "CNT - 计数器",
        "IR" => "IR - 索引寄存器",
        "DR" => "DR - 数据寄存器",
        "CF" => "CF - 脉冲标志区",
        _ => area,
    };
}
