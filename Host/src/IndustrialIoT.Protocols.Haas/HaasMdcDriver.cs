namespace IndustrialIoT.Protocols.Haas;

using System.Net.Sockets;
using System.Globalization;
using System.Text;
using System.Text.Json;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

/// <summary>
/// Haas MDC (Machine Data Collection) 驱动 — 适用于 Haas NGC 控制器。
/// 协议：TCP 9999，ASCII 指令。
///   Q 命令：读取数据（命名地址如 "Mode"→Q104；宏变量 "Macro:10001"→Q600 10001）
///   E 命令：写入宏变量（"Macro:10001" + 42.0 → E10001 42.0）
/// </summary>
[ProtocolDriver(ProtocolType.HaasMdc, "Haas", "哈斯", "HaasNGC", "MDC")]
public sealed class HaasMdcDriver : IProtocolDriver, IAddressSpaceBrowser
{
    private const int DefaultPort = 9999;
    private const int ResponseBufferSize = 1024;

    private readonly ILogger<HaasMdcDriver> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ConnectionState _state = ConnectionState.Disconnected;

    public ProtocolType Protocol => ProtocolType.HaasMdc;
    public ConnectionState State => _state;
    public DriverCapabilities Capabilities =>
        DriverCapabilities.Read | DriverCapabilities.Write |
        DriverCapabilities.Browse | DriverCapabilities.BatchRead;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public HaasMdcDriver(ILogger<HaasMdcDriver> logger) => _logger = logger;

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        if (_state == ConnectionState.Connected) return new() { Success = true };
        SetState(ConnectionState.Connecting);

        try
        {
            var port = config.Port > 0 ? config.Port : DefaultPort;
            var client = new TcpClient { ReceiveTimeout = (int)config.ReadTimeout.TotalMilliseconds };
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(config.ConnectTimeout);
                await client.ConnectAsync(config.Host, port, cts.Token);
            }

            _tcp = client;
            _stream = client.GetStream();

            _logger.LogInformation("Haas MDC connected to {Host}:{Port}", config.Host, port);
            SetState(ConnectionState.Connected);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            Cleanup();
            _logger.LogError(ex, "Haas MDC connection failed");
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
        if (_state != ConnectionState.Connected) return false;
        try
        {
            // Q100 = 读取机床序列号，任何可连通的 Haas NGC 都能响应
            var resp = await SendCommandAsync("Q100", ct);
            return !string.IsNullOrWhiteSpace(resp);
        }
        catch { return false; }
    }

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        if (!HaasCommandMap.TryBuildReadCommand(address, out var command))
            return BuildError(address, dataType, $"Unknown address '{address}' — use named key or 'Macro:xxxxx'");

        try
        {
            var raw = await SendCommandAsync(command, ct);
            var valueText = HaasCommandMap.ExtractValue(raw, command);
            return new TagValue
            {
                Address = address, DataType = dataType,
                Value = CoerceValue(valueText, dataType),
                Quality = string.IsNullOrEmpty(valueText) ? TagQuality.Uncertain : TagQuality.Good,
                Timestamp = DateTimeOffset.UtcNow,
            };
        }
        catch (Exception ex)
        {
            return BuildError(address, dataType, ex.Message);
        }
    }

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        // Haas MDC 每条命令独立发送 — 顺序处理以避免响应交错
        var results = new List<TagValue>(requests.Count);
        foreach (var req in requests)
            results.Add(await ReadTagAsync(req.Address, req.DataType, ct));
        return results;
    }

    public async Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
    {
        if (!HaasCommandMap.TryBuildWriteCommand(address, value, out var command))
            return new() { Success = false, ErrorMessage = "Haas MDC writes require 'Macro:xxxxx' address format" };

        try
        {
            var raw = await SendCommandAsync(command, ct);
            var ok = HaasCommandMap.IsWriteSuccessful(raw);
            return new() { Success = ok, ErrorMessage = ok ? null : $"Device rejected write: {raw}" };
        }
        catch (Exception ex)
        {
            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        IReadOnlyList<AddressNode> nodes = parentPath switch
        {
            null or "" => BuildRootNodes(),
            "Macro" => BuildMacroNodes(),
            "Position" => BuildSystemMacroNodes("Position:"),
            "WorkPosition" => BuildSystemMacroNodes("WorkPosition:"),
            "Spindle" => BuildSpindleNodes(),
            _ => [],
        };
        return Task.FromResult(nodes);
    }

    public async Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var nodes = BuildRootNodes()
            .Concat(BuildSystemMacroNodes("Position:"))
            .Concat(BuildSystemMacroNodes("WorkPosition:"))
            .Concat(BuildSpindleNodes())
            .Concat(BuildMacroNodes())
            .Where(n => n.NodeType == AddressNodeType.Variable)
            .ToList();
        var stream = new MemoryStream();
        if (format == ExportFormat.JSON)
            await JsonSerializer.SerializeAsync(stream, nodes, cancellationToken: ct);
        else
            await WriteCsvAsync(stream, nodes, ct);
        stream.Position = 0;
        return stream;
    }

    /// <summary>底层 Q/E 命令通道：发送 ASCII 命令，读取响应。</summary>
    private async Task<string> SendCommandAsync(string command, CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected");

        await _lock.WaitAsync(ct);
        try
        {
            var payload = Encoding.ASCII.GetBytes(command + "\r\n");
            await _stream.WriteAsync(payload, ct);

            var buffer = new byte[ResponseBufferSize];
            var read = await _stream.ReadAsync(buffer, ct);
            return Encoding.ASCII.GetString(buffer, 0, read).Trim();
        }
        finally { _lock.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        Cleanup();
        _lock.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private static IReadOnlyList<AddressNode> BuildRootNodes() =>
        HaasCommandMap.QNamedAddresses
            .Select(name => Variable(name, name, DataType.String, false))
            .Concat([
                Folder("Position", "机械坐标"),
                Folder("WorkPosition", "工件坐标"),
                Folder("Spindle", "主轴"),
                new AddressNode
                {
                    Path = "Macro",
                    DisplayName = "Macro variables",
                    NodeType = AddressNodeType.Folder,
                    IsReadable = true,
                    IsWritable = true,
                },
            ])
            .ToList();

    private static IReadOnlyList<AddressNode> BuildSystemMacroNodes(string prefix) =>
        HaasCommandMap.SystemMacroNames
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(n => Variable(n, n, DataType.Double, false))
            .ToList();

    private static IReadOnlyList<AddressNode> BuildSpindleNodes() =>
        HaasCommandMap.SystemMacroNames
            .Where(n => !n.StartsWith("Position:", StringComparison.OrdinalIgnoreCase)
                     && !n.StartsWith("WorkPosition:", StringComparison.OrdinalIgnoreCase))
            .Select(n => Variable(n, n, DataType.Double, false))
            .ToList();

    private static AddressNode Folder(string path, string display) => new()
    {
        Path = path,
        DisplayName = display,
        NodeType = AddressNodeType.Folder,
        IsReadable = true,
        IsWritable = false,
    };

    private static IReadOnlyList<AddressNode> BuildMacroNodes() =>
        Enumerable.Range(10000, 20)
            .Select(number => Variable($"Macro:{number}", $"Macro {number}", DataType.Double, true))
            .ToList();

    private static AddressNode Variable(string path, string name, DataType dataType, bool writable) => new()
    {
        Path = path,
        DisplayName = name,
        NodeType = AddressNodeType.Variable,
        DataType = dataType,
        IsReadable = true,
        IsWritable = writable,
    };

    private void Cleanup()
    {
        _stream?.Dispose(); _stream = null;
        _tcp?.Close(); _tcp?.Dispose(); _tcp = null;
    }

    private static async Task WriteCsvAsync(Stream stream, IReadOnlyList<AddressNode> nodes, CancellationToken ct)
    {
        var header = "Path,DisplayName,DataType,Readable,Writable\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(header), ct);
        foreach (var node in nodes)
        {
            var line = $"{node.Path},{node.DisplayName},{node.DataType},{node.IsReadable},{node.IsWritable}\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line), ct);
        }
    }

    private void SetState(ConnectionState next, string? reason = null)
    {
        var old = _state;
        if (old == next) return;
        _state = next;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs
        {
            OldState = old, NewState = next, Reason = reason
        });
    }

    private static TagValue BuildError(string address, DataType dataType, string error) => new()
    {
        Address = address, DataType = dataType,
        Value = string.Empty, Quality = TagQuality.Bad,
        Timestamp = DateTimeOffset.UtcNow, ErrorMessage = error,
    };

    private static object CoerceValue(string raw, DataType target) => target switch
    {
        DataType.Bool => bool.TryParse(raw, out var b) ? b : raw == "1",
        DataType.Int16 => short.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : (object)raw,
        DataType.Int32 => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : (object)raw,
        DataType.Int64 => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : (object)raw,
        DataType.Float => float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : (object)raw,
        DataType.Double => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (object)raw,
        _ => raw,
    };
}
