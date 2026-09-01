namespace IndustrialIoT.Protocols.NCLinkApi;

using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

/// <summary>
/// NC-Link 采样客户端 — 严格对照手册 8.1.22 六步法：
///   ① set_value CONSOLE "SamplSet Period N" — 设采样周期
///   ② add SYS_SMPL key=N value=[[type,axis,offset,len],...] — 注册客户端+采样项
///   ③ set_value SYS_SMPL key=N value=1 — 开启采样（=0 关闭）
///   ④ set_value REG_G index=2960 offset=12 value=1 — PLC 采样使能位
///   ⑤ get_value SYS_SMPL key=N length=N — 拉取数据（小端 int32 数组）
///   ⑥ delete SYS_SMPL key=N — 注销客户端
/// </summary>
public sealed class NCLinkApiSampleSession : IAsyncDisposable
{
    private readonly NCLinkApiClient _client;
    private readonly string _deviceId;
    private readonly string _channelKey;
    private readonly ILogger? _logger;
    private bool _running;
    private bool _registered;

    /// <summary>构造采样会话。channelKey 为采样通道字符串号（手册 0-31）。</summary>
    public NCLinkApiSampleSession(NCLinkApiClient client, string deviceId, string channelKey,
        ILogger? logger = null)
    {
        _client = client;
        _deviceId = deviceId;
        _channelKey = channelKey;
        _logger = logger;
    }

    /// <summary>
    /// 步骤 1：设置采样周期（毫秒）。
    /// 手册：set_value /MACHINE/CONTROLLER/CONSOLE value="SamplSet Period N"
    /// </summary>
    public async Task SetPeriodAsync(int periodMs, CancellationToken ct = default)
    {
        if (periodMs <= 0) throw new ArgumentOutOfRangeException(nameof(periodMs));
        await _client.SetValueAsync(
            _deviceId, NCLinkApiPaths.Console,
            JsonValue.Create($"SamplSet Period {periodMs}"), ct: ct).ConfigureAwait(false);
        _logger?.LogDebug("NCLink sample period set: {Ms}ms", periodMs);
    }

    /// <summary>
    /// 步骤 2：注册采样客户端与采样项。
    /// 每个采样项是 4 个 int32：[type, axis, offset, length]。type 见手册 Type 枚举表。
    /// </summary>
    public async Task RegisterAsync(IReadOnlyList<NCLinkSampleItem> items, CancellationToken ct = default)
    {
        if (items.Count == 0) throw new ArgumentException("items required", nameof(items));

        var valueArr = new JsonArray();
        foreach (var it in items)
        {
            valueArr.Add(new JsonArray(
                JsonValue.Create((int)it.Type),
                JsonValue.Create(it.Axis),
                JsonValue.Create(it.Offset),
                JsonValue.Create(it.Length)));
        }

        var resp = await _client.AddAsync(_deviceId, NCLinkApiPaths.VariableSysSmpl,
            key: JsonValue.Create(_channelKey), value: valueArr, ct: ct).ConfigureAwait(false);
        if (!resp.IsSuccess)
            throw new NCLinkApiException(resp.StatusCode, resp.Status,
                $"register SYS_SMPL key={_channelKey}");
        _registered = true;
        _logger?.LogDebug("NCLink sample channel {Ch} registered with {Count} items",
            _channelKey, items.Count);
    }

    /// <summary>步骤 3：开启采样。</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!_registered) throw new InvalidOperationException("Call RegisterAsync first");
        await _client.SetValueAsync(_deviceId, NCLinkApiPaths.VariableSysSmpl,
            JsonValue.Create(1),
            key: JsonValue.Create(_channelKey), ct: ct).ConfigureAwait(false);
        _running = true;
    }

    /// <summary>步骤 3 反向：关闭采样（释放资源前的标准动作）。</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_running) return;
        try
        {
            await _client.SetValueAsync(_deviceId, NCLinkApiPaths.VariableSysSmpl,
                JsonValue.Create(0),
                key: JsonValue.Create(_channelKey), ct: ct).ConfigureAwait(false);
        }
        finally { _running = false; }
    }

    /// <summary>
    /// 步骤 4：写 PLC 采样使能标记（REG_G index=2960 offset=12 value=1）。
    /// 手册说加工时会自动触发此位，离线测试需手工置 1。
    /// </summary>
    public async Task EnablePlcFlagAsync(int regGIndex = 2960, int bitOffset = 12,
        CancellationToken ct = default)
    {
        await _client.SetValueAsync(_deviceId, NCLinkApiPaths.VariableRegG,
            JsonValue.Create(1),
            index: JsonValue.Create(regGIndex), offset: bitOffset, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 步骤 5：拉取一次采样数据，返回小端 int32 数组（手册）。
    /// </summary>
    public async Task<IReadOnlyList<int>> FetchAsync(int length, CancellationToken ct = default)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));

        var resp = await _client.InvokeAsync(_deviceId, new NCLinkApiRequest
        {
            Operation = NCLinkApiOperations.GetValue,
            Items = [new NCLinkApiRequestItem
            {
                Path = NCLinkApiPaths.VariableSysSmpl,
                Key = JsonValue.Create(_channelKey),
                Length = length,
            }],
        }, ct).ConfigureAwait(false);

        if (!resp.IsSuccess)
            throw new NCLinkApiException(resp.StatusCode, resp.Status,
                $"fetch SYS_SMPL key={_channelKey} length={length}");

        // 响应 value 外层 = items(1)，内层 = int32 数组
        if (resp.Value is not JsonArray outer || outer.Count == 0) return [];
        if (outer[0] is not JsonArray inner) return [];

        var result = new int[inner.Count];
        for (var i = 0; i < inner.Count; i++)
            result[i] = inner[i]?.GetValue<int>() ?? 0;
        return result;
    }

    /// <summary>步骤 6：注销采样客户端。Dispose 时自动调用。</summary>
    public async Task UnregisterAsync(CancellationToken ct = default)
    {
        if (!_registered) return;
        try
        {
            await _client.DeleteAsync(_deviceId, NCLinkApiPaths.VariableSysSmpl,
                key: JsonValue.Create(_channelKey), ct: ct).ConfigureAwait(false);
        }
        finally { _registered = false; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_running) await StopAsync().ConfigureAwait(false);
            if (_registered) await UnregisterAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during sample session disposal");
        }
    }
}

/// <summary>
/// 采样项规格 — 对应手册 8.1.22 步骤 2 的 [type, axis, offset, length] 四元组。
/// </summary>
public readonly record struct NCLinkSampleItem(
    NCLinkSampleType Type, int Axis, int Offset, int Length);

/// <summary>
/// 手册 8.1.22 Type 枚举表。
/// </summary>
public enum NCLinkSampleType
{
    Empty = 0,
    AxisCommandPosition = 1,
    AxisActualPosition = 2,
    AxisFollowingError = 3,
    AxisCommandSpeed = 4,
    AxisActualSpeed = 5,
    AxisLoadCurrent = 6,
    CommandMotorPosition = 7,
    CommandPulsePosition = 8,
    ActualMotorPosition = 9,
    ActualPulsePosition = 10,
    Compensation = 11,
    ProgramLineNumber = 12,
    FullClosedActualPosition = 13,
    FullClosedActualSpeed = 14,
    VirtualAxisCommandPosition = 15,
    VirtualAxisActualPosition = 16,
    SystemVariable = 101,
    ChannelVariable = 102,
    AxisVariable = 103,
    RegisterX = 104,
    RegisterY = 105,
    AxisF = 106,
    AxisG = 107,
    ChannelF = 108,
    ChannelG = 109,
    SystemF = 110,
    SystemG = 111,
    RegisterR = 112,
    RegisterB = 113,
    RegisterI = 114,
    RegisterQ = 115,
    RegisterK = 116,
    RegisterW = 117,
    RegisterD = 118,
    RegisterT = 119,
    RegisterC = 120,
}
