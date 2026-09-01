namespace IndustrialIoT.Protocols.Modbus;

using System.Buffers.Binary;
using System.Text;
using FluentModbus;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using static IndustrialIoT.Protocols.Modbus.ModbusAddressParser;

[ProtocolDriver(ProtocolType.ModbusTCP, "Inovance", "汇川", "广数", "广州数控", "GSK", "MICRO-T400")]
public sealed class ModbusTcpDriver : IProtocolDriver, IAddressSpaceBrowser
{
    private readonly ILogger<ModbusTcpDriver> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private ModbusTcpClient? _client;
    private DeviceConnectionConfig? _config;
    private ConnectionState _state = ConnectionState.Disconnected;
    private byte _unitId = 1;

    public ProtocolType Protocol => ProtocolType.ModbusTCP;
    public ConnectionState State => _state;

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Read | DriverCapabilities.Write |
        DriverCapabilities.Browse | DriverCapabilities.BatchRead;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public ModbusTcpDriver(ILogger<ModbusTcpDriver> logger)
    {
        _logger = logger;
    }

    // ───────────────────────── Connection ─────────────────────────

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            if (_state == ConnectionState.Connected)
                return new() { Success = true };

            SetState(ConnectionState.Connecting);
            _config = config;

            // Modbus unit identifier / slave station. "UnitId" is the Modbus term, "Station" is what
            // the robot and Inovance UIs call it — accept either so a device configured as an Estun
            // robot keeps its station when the protocol is switched to plain Modbus TCP.
            if (TryGetStation(config, "UnitId", out var parsed) || TryGetStation(config, "Station", out parsed))
                _unitId = parsed;

            _client = new ModbusTcpClient();
            _client.ReadTimeout = (int)config.ReadTimeout.TotalMilliseconds;

            var endpoint = $"{config.Host}:{config.Port}";
            _logger.LogInformation("Connecting to Modbus TCP at {Endpoint} (unit {UnitId})...", endpoint, _unitId);

            // ModbusTcpClient.Connect is synchronous — offload to avoid blocking the caller
            await Task.Run(() => _client.Connect(endpoint), ct).WaitAsync(config.ConnectTimeout, ct);

            SetState(ConnectionState.Connected);
            _logger.LogInformation("Connected to Modbus TCP at {Endpoint}", endpoint);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            SetState(ConnectionState.Faulted, ex.Message);
            _logger.LogError(ex, "Failed to connect to Modbus TCP at {Host}:{Port}", config.Host, config.Port);
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
            if (_client is not null)
            {
                _client.Disconnect();
                _client.Dispose();
                _client = null;
            }
            SetState(ConnectionState.Disconnected);
            _logger.LogInformation("Disconnected from Modbus TCP");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_state != ConnectionState.Connected || _client is null)
            return false;

        await _semaphore.WaitAsync(ct);
        try
        {
            // Read a single holding register as a lightweight health check
            _client.ReadHoldingRegisters<short>(_unitId, 0, 1);
            return true;
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

    // ───────────────────────── Single Read ─────────────────────────

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            var parsed = Parse(address, dataType);
            var value = ReadParsed(parsed, dataType);
            return new()
            {
                Address = address,
                DataType = dataType,
                Value = value,
                Quality = TagQuality.Good,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReadTag failed for {Address} ({DataType})", address, dataType);
            return new()
            {
                Address = address,
                DataType = dataType,
                Value = 0,
                Quality = TagQuality.Bad,
                Timestamp = DateTimeOffset.UtcNow,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ───────────────────────── Batch Read ─────────────────────────

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(
        IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        EnsureConnected();

        var results = new TagValue[requests.Count];

        // Parse up front, but keep a malformed address from aborting the whole batch —
        // it should surface as one Bad tag, not a failed request for every other tag.
        var parsedItems = new List<(TagReadRequest Req, int Idx, ParsedAddress Parsed)>(requests.Count);
        for (int i = 0; i < requests.Count; i++)
        {
            var req = requests[i];
            try
            {
                parsedItems.Add((req, i, Parse(req.Address, req.DataType)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Batch read address parse failed for {Address}", req.Address);
                results[i] = new()
                {
                    Address = req.Address,
                    DataType = req.DataType,
                    Value = 0,
                    Quality = TagQuality.Bad,
                    Timestamp = DateTimeOffset.UtcNow,
                    ErrorMessage = ex.Message
                };
            }
        }

        // Group by register type so we can potentially batch contiguous addresses
        var grouped = parsedItems.GroupBy(x => x.Parsed.RegisterType);

        await _semaphore.WaitAsync(ct);
        try
        {
            foreach (var group in grouped)
            {
                foreach (var item in group)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var value = ReadParsed(item.Parsed, item.Req.DataType);
                        results[item.Idx] = new()
                        {
                            Address = item.Req.Address,
                            DataType = item.Req.DataType,
                            Value = value,
                            Quality = TagQuality.Good,
                            Timestamp = DateTimeOffset.UtcNow
                        };
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Batch read failed for {Address}", item.Req.Address);
                        results[item.Idx] = new()
                        {
                            Address = item.Req.Address,
                            DataType = item.Req.DataType,
                            Value = 0,
                            Quality = TagQuality.Bad,
                            Timestamp = DateTimeOffset.UtcNow,
                            ErrorMessage = ex.Message
                        };
                    }
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }

        return results;
    }

    // ───────────────────────── Write ─────────────────────────

    public async Task<WriteResult> WriteTagAsync(
        string address, DataType dataType, object value, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            var parsed = Parse(address, dataType);
            WriteParsed(parsed, dataType, value);
            _logger.LogDebug("Write succeeded: {Address} = {Value} ({DataType})", address, value, dataType);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WriteTag failed for {Address}", address);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ───────────────────────── Browse ─────────────────────────

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        IReadOnlyList<AddressNode> nodes;

        if (string.IsNullOrEmpty(parentPath))
        {
            // Top-level folders for Inovance PLC register regions
            nodes =
            [
                MakeFolder("HR", "保持寄存器 (Holding Registers)"),
                MakeFolder("IR", "输入寄存器 (Input Registers)"),
                MakeFolder("C",  "线圈 (Coils)"),
                MakeFolder("DI", "离散输入 (Discrete Inputs)"),
            ];
        }
        else
        {
            nodes = parentPath.ToUpperInvariant() switch
            {
                "HR" => GenerateRegisterNodes("HR", "保持寄存器", DataType.Int16, 0, 999, writable: true),
                "IR" => GenerateRegisterNodes("IR", "输入寄存器", DataType.Int16, 0, 999, writable: false),
                "C"  => GenerateRegisterNodes("C",  "线圈",       DataType.Bool,  0, 999, writable: true),
                "DI" => GenerateRegisterNodes("DI", "离散输入",   DataType.Bool,  0, 999, writable: false),
                _ => []
            };
        }

        return Task.FromResult(nodes);
    }

    public async Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Path,DisplayName,DataType,Readable,Writable");

        var folders = await BrowseAsync(null, ct);
        foreach (var folder in folders)
        {
            var children = await BrowseAsync(folder.Path, ct);
            foreach (var node in children)
            {
                sb.AppendLine(
                    $"{node.Path},{node.DisplayName},{node.DataType},{node.IsReadable},{node.IsWritable}");
            }
        }

        Stream stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        return stream;
    }

    // ───────────────────────── Dispose ─────────────────────────

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during DisposeAsync");
        }
        _semaphore.Dispose();
    }

    // ═══════════════════════ Private Helpers ═══════════════════════

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected || _client is null)
            throw new InvalidOperationException("Modbus TCP client is not connected.");
    }

    private static bool TryGetStation(DeviceConnectionConfig config, string key, out byte value)
    {
        value = 0;
        return config.ExtendedProperties.TryGetValue(key, out var raw)
            && byte.TryParse(raw, out value)
            && value > 0;
    }

    private void SetState(ConnectionState newState, string? reason = null)
    {
        var old = _state;
        if (old == newState) return;
        _state = newState;
        StateChanged?.Invoke(this, new() { OldState = old, NewState = newState, Reason = reason });
    }

    // ─────────── Low-level read dispatch ───────────

    private object ReadParsed(ParsedAddress parsed, DataType dataType)
    {
        return parsed.RegisterType switch
        {
            ModbusRegisterType.Coil =>
                ReadBits(_client!.ReadCoils(_unitId, parsed.StartAddress, parsed.RegisterCount).ToArray(), parsed),

            ModbusRegisterType.DiscreteInput =>
                ReadBits(_client!.ReadDiscreteInputs(_unitId, parsed.StartAddress, parsed.RegisterCount).ToArray(), parsed),

            ModbusRegisterType.HoldingRegister =>
                ReadRegisterValue(() => _client!.ReadHoldingRegisters(
                    _unitId,
                    checked((ushort)parsed.StartAddress),
                    checked((ushort)parsed.RegisterCount)).ToArray(), dataType, parsed),

            ModbusRegisterType.InputRegister =>
                ReadRegisterValue(() => _client!.ReadInputRegisters(
                    _unitId,
                    checked((ushort)parsed.StartAddress),
                    checked((ushort)parsed.RegisterCount)).ToArray(), dataType, parsed),

            _ => throw new NotSupportedException($"Register type {parsed.RegisterType} is not supported.")
        };
    }

    /// <summary>
    /// 读线圈 / 离散输入。FluentModbus 按位打包返回字节，这里展开成布尔。
    /// 单点（默认）返回 <see cref="bool"/>，带 <c>;N</c> 数量后缀时返回 <c>bool[]</c>。
    /// </summary>
    private static object ReadBits(byte[] packed, ParsedAddress parsed)
    {
        if (!parsed.CountExplicit)
            return (packed[0] & 0x01) != 0;

        var bits = new bool[parsed.RegisterCount];
        for (int i = 0; i < bits.Length; i++)
            bits[i] = (packed[i / 8] & (1 << (i % 8))) != 0;
        return bits;
    }

    /// <summary>
    /// Reads raw register bytes and converts to the target CLR type.
    /// FluentModbus returns bytes in big-endian (network) order.
    ///
    /// 带 <c>;N</c> 数量后缀时（<see cref="ParsedAddress.CountExplicit"/>），N 是寄存器个数，
    /// 返回目标类型的<b>数组</b> —— 例如 <c>40001;100</c> 配 UInt16 返回 100 个值，配 Float 返回 50 个。
    /// 这是通用 Modbus 调试工具"读一个寄存器窗口"的行为。
    /// </summary>
    private static object ReadRegisterValue(Func<byte[]> readFunc, DataType dataType, ParsedAddress parsed)
    {
        var raw = readFunc();
        var span = raw.AsSpan();

        // ByteArray / String 天然就是变长的，直接吃掉整段
        if (dataType == DataType.ByteArray) return span.ToArray();
        if (dataType == DataType.String) return Encoding.ASCII.GetString(span).TrimEnd('\0');

        if (!parsed.CountExplicit)
            return ReadScalar(span, dataType);

        var perElement = ModbusAddressParser.GetRegisterCount(parsed.RegisterType, dataType) * 2;
        if (raw.Length % perElement != 0)
            throw new FormatException(
                $"请求了 {parsed.RegisterCount} 个寄存器（{raw.Length} 字节），无法按 {dataType} " +
                $"（每个占 {perElement / 2} 个寄存器）整除。请把数量改成 {perElement / 2} 的倍数。");

        var count = raw.Length / perElement;
        var values = Array.CreateInstance(ScalarClrType(dataType), count);
        for (int i = 0; i < count; i++)
            values.SetValue(ReadScalar(span.Slice(i * perElement, perElement), dataType), i);
        return values;
    }

    private static object ReadScalar(ReadOnlySpan<byte> span, DataType dataType) => dataType switch
    {
        DataType.Bool   => BinaryPrimitives.ReadInt16BigEndian(span) != 0,
        DataType.Int16  => BinaryPrimitives.ReadInt16BigEndian(span),
        DataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(span),
        DataType.Int32  => BinaryPrimitives.ReadInt32BigEndian(span),
        DataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(span),
        DataType.Float  => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(span)),
        DataType.Int64  => BinaryPrimitives.ReadInt64BigEndian(span),
        DataType.Double => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(span)),
        _ => throw new NotSupportedException($"DataType {dataType} is not supported for register reads.")
    };

    private static Type ScalarClrType(DataType dataType) => dataType switch
    {
        DataType.Bool   => typeof(bool),
        DataType.Int16  => typeof(short),
        DataType.UInt16 => typeof(ushort),
        DataType.Int32  => typeof(int),
        DataType.UInt32 => typeof(uint),
        DataType.Float  => typeof(float),
        DataType.Int64  => typeof(long),
        DataType.Double => typeof(double),
        _ => throw new NotSupportedException($"DataType {dataType} has no scalar CLR mapping.")
    };

    // ─────────── Low-level write dispatch ───────────

    private void WriteParsed(ParsedAddress parsed, DataType dataType, object value)
    {
        switch (parsed.RegisterType)
        {
            case ModbusRegisterType.Coil:
                _client!.WriteSingleCoil(_unitId, parsed.StartAddress, Convert.ToBoolean(value));
                break;

            case ModbusRegisterType.HoldingRegister:
                WriteHoldingRegister(parsed, dataType, value);
                break;

            case ModbusRegisterType.DiscreteInput:
            case ModbusRegisterType.InputRegister:
                throw new InvalidOperationException(
                    $"Register type {parsed.RegisterType} is read-only and cannot be written.");

            default:
                throw new NotSupportedException($"Register type {parsed.RegisterType} is not supported for writes.");
        }
    }

    private void WriteHoldingRegister(ParsedAddress parsed, DataType dataType, object value)
    {
        if (parsed.RegisterCount == 1)
        {
            short raw = dataType switch
            {
                DataType.Bool   => (short)(Convert.ToBoolean(value) ? 1 : 0),
                DataType.Int16  => Convert.ToInt16(value),
                DataType.UInt16 => unchecked((short)Convert.ToUInt16(value)),
                _ => Convert.ToInt16(value)
            };
            _client!.WriteSingleRegister(_unitId, parsed.StartAddress, raw);
        }
        else
        {
            var bytes = new byte[parsed.RegisterCount * 2];
            switch (dataType)
            {
                case DataType.Int32:
                    BinaryPrimitives.WriteInt32BigEndian(bytes, Convert.ToInt32(value));
                    break;
                case DataType.UInt32:
                    BinaryPrimitives.WriteUInt32BigEndian(bytes, Convert.ToUInt32(value));
                    break;
                case DataType.Float:
                    BinaryPrimitives.WriteInt32BigEndian(bytes,
                        BitConverter.SingleToInt32Bits(Convert.ToSingle(value)));
                    break;
                case DataType.Int64:
                    BinaryPrimitives.WriteInt64BigEndian(bytes, Convert.ToInt64(value));
                    break;
                case DataType.Double:
                    BinaryPrimitives.WriteInt64BigEndian(bytes,
                        BitConverter.DoubleToInt64Bits(Convert.ToDouble(value)));
                    break;
                default:
                    throw new NotSupportedException(
                        $"DataType {dataType} is not supported for multi-register writes.");
            }
            // Convert big-endian bytes to short[] for FluentModbus WriteMultipleRegisters
            var registers = new short[parsed.RegisterCount];
            for (int i = 0; i < registers.Length; i++)
                registers[i] = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(i * 2, 2));
            _client!.WriteMultipleRegisters(_unitId, parsed.StartAddress, registers);
        }
    }

    // ─────────── Browse helpers ───────────

    private static AddressNode MakeFolder(string path, string displayName) =>
        new()
        {
            Path = path,
            DisplayName = displayName,
            NodeType = AddressNodeType.Folder,
            IsReadable = false,
            IsWritable = false
        };

    private static IReadOnlyList<AddressNode> GenerateRegisterNodes(
        string prefix, string label, DataType dataType, int from, int to, bool writable)
    {
        var list = new List<AddressNode>(to - from + 1);
        for (int i = from; i <= to; i++)
        {
            list.Add(new()
            {
                Path = $"{prefix}{i}",
                DisplayName = $"{prefix}{i} {label}",
                NodeType = AddressNodeType.Variable,
                DataType = dataType,
                IsReadable = true,
                IsWritable = writable
            });
        }
        return list;
    }
}
