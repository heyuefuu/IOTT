namespace IndustrialIoT.Protocols.Gsk;

using System.Text;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

/// <summary>
/// 广数 GSK RM 数据采集驱动 — 适用于 GSK 980/25i/MICRO-T400 等系统。
///
/// 通道：TCP，端口默认走 SDK 内部约定（可用 config.Port 覆写）。
/// 命名地址写法见 <see cref="GskrmAddressMapper"/> 类顶部注释。
///
/// 文件传输使用独立的 <see cref="GskrmTransferDriver"/>，两者不共享句柄。
/// </summary>
[ProtocolDriver(ProtocolType.Gskrm, "广数", "广州数控", "GSK", "MICRO-T400", "980", "25i")]
public sealed class GskrmDriver : IProtocolDriver, IAddressSpaceBrowser
{
    private const int DefaultPort = 6000; // [unverified] SDK-default — revisit with real hardware
    private const int DefaultTimeoutMs = 5_000;

    private readonly ILogger<GskrmDriver> _logger;
    private readonly IGskrmApi _api;
    private readonly GskrmAddressMapper _mapper;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private DeviceConnectionConfig? _config;
    private int _handle;
    private ConnectionState _state = ConnectionState.Disconnected;

    public ProtocolType Protocol => ProtocolType.Gskrm;
    public ConnectionState State => _state;
    public DriverCapabilities Capabilities =>
        DriverCapabilities.Read | DriverCapabilities.Write |
        DriverCapabilities.Browse | DriverCapabilities.BatchRead;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public GskrmDriver(ILogger<GskrmDriver> logger, IGskrmApi? api = null)
    {
        _logger = logger;
        _api = api ?? new NativeGskrmApi();
        _mapper = new GskrmAddressMapper(_api);
    }

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_state == ConnectionState.Connected) return new() { Success = true };
            SetState(ConnectionState.Connecting);
            _config = config;

            var port = config.Port > 0 ? config.Port : DefaultPort;
            var timeout = config.ConnectTimeout.TotalMilliseconds > 0
                ? (int)config.ConnectTimeout.TotalMilliseconds : DefaultTimeoutMs;

            _logger.LogInformation("Connecting to GSK RM at {Host}:{Port} (timeout {Timeout}ms)...",
                config.Host, port, timeout);

            int rc = await Task.Run(() => _api.CreateInstance(config.Host, port, timeout, out _handle), ct);
            if (rc != GskrmErrorCodes.Ok || _handle <= 0)
            {
                var msg = $"GSKRM_CreateInstance failed: {GskrmErrorCodes.Describe(rc)}";
                SetState(ConnectionState.Faulted, msg);
                _logger.LogError("GSKRM connect failed: {Msg}", msg);
                return new() { Success = false, ErrorMessage = msg };
            }

            _api.SetOvertime(_handle, (int)config.ReadTimeout.TotalMilliseconds);
            _mapper.SetHandle(_handle);
            SetState(ConnectionState.Connected);
            _logger.LogInformation("GSKRM connected (handle={Handle})", _handle);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            SetState(ConnectionState.Faulted, ex.Message);
            _logger.LogError(ex, "GSKRM connect failed");
            return new() { Success = false, ErrorMessage = ex.Message };
        }
        finally { _lock.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_state == ConnectionState.Disconnected) return;
            if (_handle > 0) _api.CloseInstance(_handle);
            _handle = 0;
            SetState(ConnectionState.Disconnected);
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_state != ConnectionState.Connected) return false;
        await _lock.WaitAsync(ct);
        try
        {
            return _api.GetConnectState(_handle, out var connected) == GskrmErrorCodes.Ok && connected;
        }
        catch { return false; }
        finally { _lock.Release(); }
    }

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        EnsureConnected();
        await _lock.WaitAsync(ct);
        try { return _mapper.Read(address, dataType); }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        EnsureConnected();
        await _lock.WaitAsync(ct);
        try
        {
            var results = new List<TagValue>(requests.Count);
            foreach (var req in requests) results.Add(_mapper.Read(req.Address, req.DataType));
            return results;
        }
        finally { _lock.Release(); }
    }

    public async Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
    {
        EnsureConnected();
        await _lock.WaitAsync(ct);
        try { return _mapper.Write(address, dataType, value); }
        finally { _lock.Release(); }
    }

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        IReadOnlyList<AddressNode> nodes = string.IsNullOrWhiteSpace(parentPath) || parentPath == "/"
            ? BuildRootNodes()
            : BuildChildNodes(parentPath.Trim('/'));
        return Task.FromResult(nodes);
    }

    public Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Path,DisplayName,DataType,Readable,Writable");
        foreach (var node in FlattenAll())
            sb.AppendLine($"{node.Path},{node.DisplayName},{node.DataType},{node.IsReadable},{node.IsWritable}");
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _lock.Dispose();
    }

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected)
            throw new InvalidOperationException("GSKRM driver is not connected.");
    }

    private void SetState(ConnectionState next, string? reason = null)
    {
        var old = _state; if (old == next) return;
        _state = next;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs { OldState = old, NewState = next, Reason = reason });
    }

    // ── Address-space tree ──────────────────────────────────────────
    private static IReadOnlyList<AddressNode> BuildRootNodes() =>
    [
        Folder("/Status", "运行状态"),
        Folder("/Rate",   "倍率"),
        Folder("/Speed",  "速度"),
        Folder("/Position", "坐标"),
        Folder("/Macro",  "宏变量"),
        Folder("/Param",  "参数"),
        Folder("/Plc",    "PLC"),
        Folder("/Tool",   "刀具"),
        Folder("/Part",   "产量"),
        Folder("/Time",   "时间"),
    ];

    private static IReadOnlyList<AddressNode> BuildChildNodes(string group) => group.ToLowerInvariant() switch
    {
        "status" =>
        [
            Variable("Status.Running",     "Running",    DataType.Bool),
            Variable("Status.Mode",        "WorkMode",   DataType.String),
            Variable("Status.ProgramName", "Program",    DataType.String),
            Variable("Status.LineNo",      "LineNo",     DataType.Int32),
            Variable("Status.Estop",       "Estop",      DataType.Bool, writable: false),
        ],
        "rate" =>
        [
            Variable("Rate.Feed",     "Feed",     DataType.Int32, writable: false),
            Variable("Rate.Fast",     "Fast",     DataType.Int32, writable: false),
            Variable("Rate.Jog",      "Jog",      DataType.Int32, writable: false),
            Variable("Rate.Spindle",  "Spindle",  DataType.Int32, writable: false),
            Variable("Rate.HandWheel","HandWheel",DataType.Int32, writable: false),
        ],
        "speed" =>
        [
            Variable("Speed.FeedAct",    "FeedAct",    DataType.Int32, writable: false),
            Variable("Speed.FeedProg",   "FeedProg",   DataType.Int32, writable: false),
            Variable("Speed.SpindleAct", "SpindleAct", DataType.Int32, writable: false),
            Variable("Speed.SpindleProg","SpindleProg",DataType.Int32, writable: false),
        ],
        "position" =>
        [
            Variable("Position.Abs:0",     "Abs axis 0",     DataType.Double, writable: false),
            Variable("Position.Machine:0", "Machine axis 0", DataType.Double, writable: false),
            Variable("Position.Relative:0","Rel axis 0",     DataType.Double, writable: false),
        ],
        "part" => [ Variable("Part.Count", "Count", DataType.Int32, writable: false) ],
        "time" =>
        [
            Variable("Time.Cut", "Cut (seconds)", DataType.Int32, writable: false),
            Variable("Time.Run", "Run (seconds)", DataType.Int32, writable: false),
        ],
        "tool" =>
        [
            Variable("Tool.OffsetCount","OffsetCount", DataType.Int32, writable: false),
            Variable("Tool.Offset:1",   "Offset[1]",   DataType.Double, writable: false),
        ],
        // Macro/Param/Plc 是用户自由输入地址（Macro:10001 等），默认不展开子节点。
        _ => []
    };

    private IEnumerable<AddressNode> FlattenAll()
    {
        foreach (var folder in BuildRootNodes())
            foreach (var leaf in BuildChildNodes(folder.Path.Trim('/')))
                yield return leaf;
    }

    private static AddressNode Folder(string path, string name) => new()
    {
        Path = path, DisplayName = name, NodeType = AddressNodeType.Folder, IsReadable = false, IsWritable = false
    };

    private static AddressNode Variable(string path, string name, DataType type, bool writable = true) => new()
    {
        Path = "/" + path, DisplayName = name, NodeType = AddressNodeType.Variable,
        DataType = type, IsReadable = true, IsWritable = writable
    };
}
