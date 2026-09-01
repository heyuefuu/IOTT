namespace IndustrialIoT.Protocols.EstunRobot;

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HslCommunication;
using HslCommunication.Robot.Estun;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

/// <summary>
/// 埃斯顿机器人驱动（ER / ProNet 系列控制器），基于 HslCommunication <see cref="EstunTcpNet"/>。
/// 底层为 Modbus/TCP，默认端口 502，站号由 ExtendedProperties["Station"] 指定（默认 1）。
///
/// 地址语法：
///   整机快照   ESTUN_DATA                 → JSON 字符串（只读）
///   状态字段   DATA:&lt;EstunData 属性名&gt;    → 如 DATA:ErrorStatus、DATA:GlobalSpeedValue、DATA:ProjectName
///   IO 位/值   DI0..DI63、DO0..DO63       → Bool（只读）
///              AI0..AI31、AO0..AO31       → Float（只读）
///   机器人指令 CMD:Start / CMD:Stop / CMD:ResetError / CMD:LoadProject /
///              CMD:UnregisterProject / CMD:CommandStatusRestart  → 只写
///   原始地址   直传 Hsl Modbus 语法，如 "36"、"4x100"、"1000;float"、"0x0040"
///
/// 上述 ESTUN_DATA / DATA: / IO 地址同源于一次 <see cref="EstunTcpNet.ReadRobotData"/> 调用，
/// 驱动内按 SnapshotTtlMs（默认 200ms）缓存快照，因此批量读 64 个 DI 位只产生一次报文往返。
///
/// 注意：快照 IO 为只读投影 —— Hsl 未提供按位写回接口，写 DO/AO 请使用对应的原始 Modbus 线圈/寄存器地址。
/// </summary>
[ProtocolDriver(ProtocolType.EstunRobot, "埃斯顿", "ESTUN", "Estun", "Robot", "机器人", "ER", "ProNet")]
public sealed class EstunRobotDriver : IProtocolDriver, IAddressSpaceBrowser
{
    private const int DefaultPort = 502;

    /// <summary>原始 Modbus 直传读写字符串时的寄存器长度，与 Inovance / FINS / Mewtocol 等驱动保持一致。</summary>
    private const ushort DefaultStringLength = 16;

    private static readonly Regex IoRegex = new(
        @"^(?<area>DI|DO|AI|AO)(?<idx>\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILogger<EstunRobotDriver> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private EstunTcpNet? _client;
    private ConnectionState _state = ConnectionState.Disconnected;
    private TimeSpan _pingTimeout = TimeSpan.FromMilliseconds(3000);
    private TimeSpan _snapshotTtl = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// <see cref="EstunTcpNet.ReadRobotData"/> 的硬超时。Hsl 内部在报文校验不匹配时会持续重收，
    /// 单次调用最坏可达 ReceiveTimeOut 的数倍，且同步阻塞、不响应取消，必须在外层兜底。
    /// </summary>
    private TimeSpan _snapshotTimeout = TimeSpan.FromSeconds(10);

    private EstunData? _snapshot;
    private DateTimeOffset _snapshotAt = DateTimeOffset.MinValue;

    public EstunRobotDriver(ILogger<EstunRobotDriver> logger) => _logger = logger;

    public ProtocolType Protocol => ProtocolType.EstunRobot;
    public ConnectionState State => _state;

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Read | DriverCapabilities.Write |
        DriverCapabilities.Browse | DriverCapabilities.BatchRead;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        if (_state == ConnectionState.Connected) return new() { Success = true };
        SetState(ConnectionState.Connecting);

        try
        {
            var port = config.Port > 0 ? config.Port : DefaultPort;
            var station = TryGetByte(config, "Station", out var s) ? s : (byte)1;

            var client = new EstunTcpNet(config.Host, port, station)
            {
                ConnectTimeOut = (int)config.ConnectTimeout.TotalMilliseconds,
                ReceiveTimeOut = (int)config.ReadTimeout.TotalMilliseconds,
            };

            // EstunTcpNet(ip, port, station) 只赋 IpAddress/Port，station 形参被直接丢弃
            // （见 Hsl 源码），必须显式设置，否则站号永远是默认 1。站号不符时机器人的响应
            // 会被 CheckMessageMatch 判为不匹配，Hsl 持续重收直到超时 —— 表现为读取长时间挂住。
            client.Station = station;

            DisableHslKeepAliveTimer(client);

            var r = await client.ConnectServerAsync();
            if (!r.IsSuccess) throw new InvalidOperationException(r.Message);

            if (TryGetPositiveInt(config, "PingTimeout", out var pt)) _pingTimeout = TimeSpan.FromMilliseconds(pt);
            if (TryGetPositiveInt(config, "SnapshotTtlMs", out var ttl)) _snapshotTtl = TimeSpan.FromMilliseconds(ttl);
            _snapshotTimeout = TryGetPositiveInt(config, "SnapshotTimeoutMs", out var st)
                ? TimeSpan.FromMilliseconds(st)
                : Max(config.ReadTimeout * 2, TimeSpan.FromSeconds(5));

            Cleanup();
            _client = client;

            _logger.LogInformation("Estun Robot connected to {Host}:{Port} station={Station}", config.Host, port, station);
            SetState(ConnectionState.Connected);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            Cleanup();
            _logger.LogError(ex, "Estun Robot connection failed");
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
        if (_state != ConnectionState.Connected || _client is null) return false;
        bool acquired = false;
        try
        {
            acquired = await _semaphore.WaitAsync(_pingTimeout, ct);

            // 拿不到锁说明另一次读写正在进行 —— 连接是活的，只是忙。
            // 报 false 会让连接池把一条正在使用的连接判定为掉线并关掉。
            if (!acquired) return true;

            return (await RefreshSnapshotAsync(force: true, ct)).IsSuccess;
        }
        catch { return false; }
        finally { if (acquired) _semaphore.Release(); }
    }

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        if (_state != ConnectionState.Connected || _client is null)
            return Bad(address, dataType, "埃斯顿机器人驱动未连接");

        await _semaphore.WaitAsync(ct);
        try
        {
            return await ReadCoreAsync(address, dataType, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Estun Robot read failed at {Address}", address);
            return Bad(address, dataType, ex.Message);
        }
        finally { _semaphore.Release(); }
    }

    /// <summary>
    /// 批量读。整批共用一次快照 —— 只要请求里含任一快照地址，就先强制刷新一次并把结果透传给每一条，
    /// 因此无论 SnapshotTtlMs 设多小，同一批次内只产生一次 ReadRobotData 往返；原始 Modbus 地址仍按条往返。
    /// </summary>
    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(
        IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        var results = new List<TagValue>(requests.Count);
        if (_state != ConnectionState.Connected || _client is null)
        {
            foreach (var req in requests) results.Add(Bad(req.Address, req.DataType, "埃斯顿机器人驱动未连接"));
            return results;
        }

        await _semaphore.WaitAsync(ct);
        try
        {
            EstunData? batch = null;
            string? snapshotError = null;
            if (requests.Any(r => IsSnapshotAddress(r.Address)))
            {
                var snap = await RefreshSnapshotAsync(force: true, ct);
                if (snap.IsSuccess) batch = snap.Content;
                else snapshotError = snap.Message;
            }

            foreach (var req in requests)
            {
                ct.ThrowIfCancellationRequested();

                // 整批快照已经取失败了，逐条重试只会把一次超时放大成 N 次
                if (snapshotError is not null && IsSnapshotAddress(req.Address))
                {
                    results.Add(Bad(req.Address, req.DataType, snapshotError));
                    continue;
                }

                try { results.Add(await ReadCoreAsync(req.Address, req.DataType, batch, ct)); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Estun Robot batch read failed at {Address}", req.Address);
                    results.Add(Bad(req.Address, req.DataType, ex.Message));
                }
            }
            return results;
        }
        finally { _semaphore.Release(); }
    }

    public async Task<WriteResult> WriteTagAsync(
        string address, DataType dataType, object value, CancellationToken ct = default)
    {
        if (_state != ConnectionState.Connected || _client is null)
            return new() { Success = false, ErrorMessage = "埃斯顿机器人驱动未连接" };

        await _semaphore.WaitAsync(ct);
        try
        {
            var addr = address.Trim();

            if (addr.StartsWith(EstunAddressSpace.CommandPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var name = addr[EstunAddressSpace.CommandPrefix.Length..];
                if (!EstunAddressSpace.Commands.TryGetValue(name, out var cmd))
                    return new() { Success = false, ErrorMessage = $"未知的埃斯顿机器人指令 '{name}'" };
                return await ExecuteCommandAsync(cmd, value);
            }

            if (addr.StartsWith(EstunAddressSpace.DataPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var name = addr[EstunAddressSpace.DataPrefix.Length..];
                if (!EstunAddressSpace.Fields.ContainsKey(name))
                    return new() { Success = false, ErrorMessage = $"未知的埃斯顿状态字段 '{name}'" };
                if (!EstunAddressSpace.WritableFields.TryGetValue(name, out var cmd))
                    return new() { Success = false, ErrorMessage = $"埃斯顿状态字段 '{name}' 为只读" };
                return await ExecuteCommandAsync(cmd, value);
            }

            if (addr.Equals(EstunAddressSpace.SnapshotAddress, StringComparison.OrdinalIgnoreCase) || IoRegex.IsMatch(addr))
                return new()
                {
                    Success = false,
                    ErrorMessage = $"'{address}' 为快照只读投影；写 IO 请使用原始 Modbus 线圈/寄存器地址（如 \"0x0040\"）",
                };

            return await WriteRawAsync(addr, dataType, value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Estun Robot write failed at {Address}", address);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
        finally { _semaphore.Release(); }
    }

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
        => Task.FromResult(EstunAddressSpace.Browse(parentPath));

    public Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        string text;
        if (format == ExportFormat.JSON)
        {
            text = JsonSerializer.Serialize(
                EstunAddressSpace.Enumerate().Select(n => new
                {
                    n.Path, n.DisplayName, DataType = n.DataType.ToString(), n.Readable, n.Writable,
                }),
                new JsonSerializerOptions { WriteIndented = true });
        }
        else
        {
            var sb = new StringBuilder("Path,DisplayName,DataType,Readable,Writable\n");
            foreach (var n in EstunAddressSpace.Enumerate())
                sb.Append(n.Path).Append(',').Append(n.DisplayName).Append(',')
                  .Append(n.DataType).Append(',').Append(n.Readable).Append(',').Append(n.Writable).Append('\n');
            text = sb.ToString();
        }

        Stream s = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return Task.FromResult(s);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- 读取核心（调用方须持有 _semaphore）----

    /// <summary>
    /// 读取单个地址。<paramref name="batchSnapshot"/> 非空时（批量读路径）直接复用该快照，
    /// 不再按 TTL 重新判定，保证一批只读一次机器人。
    /// </summary>
    private async Task<TagValue> ReadCoreAsync(
        string address, DataType dataType, EstunData? batchSnapshot = null, CancellationToken ct = default)
    {
        var addr = address.Trim();

        if (addr.StartsWith(EstunAddressSpace.CommandPrefix, StringComparison.OrdinalIgnoreCase))
            return Bad(address, dataType, $"'{address}' 为只写指令地址");

        if (addr.Equals(EstunAddressSpace.SnapshotAddress, StringComparison.OrdinalIgnoreCase))
        {
            var snap = await GetSnapshotAsync(batchSnapshot, ct);
            return snap.IsSuccess
                ? Ok(address, DataType.String, JsonSerializer.Serialize(snap.Content))
                : Bad(address, dataType, snap.Message);
        }

        if (addr.StartsWith(EstunAddressSpace.DataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = addr[EstunAddressSpace.DataPrefix.Length..];
            if (!EstunAddressSpace.Fields.TryGetValue(name, out var field))
                return Bad(address, dataType, $"未知的埃斯顿状态字段 '{name}'");

            var snap = await GetSnapshotAsync(batchSnapshot, ct);
            if (!snap.IsSuccess) return Bad(address, dataType, snap.Message);

            var raw = field.Property.GetValue(snap.Content);
            var val = field.DataType == DataType.String && raw is not string
                ? JsonSerializer.Serialize(raw)
                : raw;
            return val is null
                ? Bad(address, dataType, $"字段 '{name}' 返回空值")
                : Ok(address, field.DataType, val);
        }

        var m = IoRegex.Match(addr);
        if (m.Success)
        {
            var area = m.Groups["area"].Value.ToUpperInvariant();
            if (!int.TryParse(m.Groups["idx"].Value, out var idx))
                return Bad(address, dataType, $"IO 索引 '{m.Groups["idx"].Value}' 无效");

            var snap = await GetSnapshotAsync(batchSnapshot, ct);
            if (!snap.IsSuccess) return Bad(address, dataType, snap.Message);

            var data = snap.Content;
            return area switch
            {
                "DI" => FromBoolArray(address, dataType, data.DI, idx, "DI"),
                "DO" => FromBoolArray(address, dataType, data.DO, idx, "DO"),
                "AI" => FromFloatArray(address, dataType, data.AI, idx, "AI"),
                "AO" => FromFloatArray(address, dataType, data.AO, idx, "AO"),
                _ => Bad(address, dataType, $"未知 IO 区域 '{area}'"),
            };
        }

        return await ReadRawAsync(address, addr, dataType);
    }

    /// <summary>批量读路径下复用整批快照；否则按 TTL 决定是否重新拉取。</summary>
    private Task<OperateResult<EstunData>> GetSnapshotAsync(EstunData? batchSnapshot, CancellationToken ct = default) =>
        batchSnapshot is not null
            ? Task.FromResult(OperateResult.CreateSuccessResult(batchSnapshot))
            : RefreshSnapshotAsync(force: false, ct);

    /// <summary>
    /// 刷新 EstunData 快照。<see cref="EstunTcpNet.ReadRobotData"/> 只有同步版本（内部就是一次
    /// <c>Read("0", 100)</c>，读 40001~40100 共 100 个保持寄存器），这里是驱动内唯一的同步→异步桥接点。
    ///
    /// 它不响应取消，且 Hsl 在响应校验不匹配时会循环重收（超时判定在每次 receive 之后才做），
    /// 单次调用最坏可达 ReceiveTimeOut 的数倍。因此这里用 <see cref="_snapshotTimeout"/> 硬性兜底：
    /// 超时即返回失败，让调用方拿到明确错误，而不是把 HTTP 请求挂到客户端自己放弃。
    /// 被放弃的后台任务无法中断（同步 API），但其异常会被观测掉以免进程级 unobserved 异常。
    /// </summary>
    private async Task<OperateResult<EstunData>> RefreshSnapshotAsync(bool force, CancellationToken ct = default)
    {
        if (!force && _snapshot is not null && DateTimeOffset.UtcNow - _snapshotAt < _snapshotTtl)
            return OperateResult.CreateSuccessResult(_snapshot);

        var client = _client!;
        var readTask = Task.Run(() => client.ReadRobotData(), CancellationToken.None);

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeoutTask = Task.Delay(_snapshotTimeout, delayCts.Token);
        var winner = await Task.WhenAny(readTask, timeoutTask);
        delayCts.Cancel();

        if (winner != readTask)
        {
            _ = readTask.ContinueWith(
                t => _logger.LogWarning(
                    t.Exception,
                    "Estun Robot 快照读取在 {Timeout}ms 后被放弃，后台任务随后结束",
                    _snapshotTimeout.TotalMilliseconds),
                TaskContinuationOptions.ExecuteSynchronously);

            _snapshot = null;
            ct.ThrowIfCancellationRequested();
            return new OperateResult<EstunData>(
                $"读取埃斯顿机器人快照超时（{_snapshotTimeout.TotalMilliseconds:0}ms）。" +
                "请确认站号（ExtendedProperties[\"Station\"]）与控制器一致，且 40001~40100 保持寄存器可读。");
        }

        var result = await readTask;
        if (result.IsSuccess)
        {
            _snapshot = result.Content;
            _snapshotAt = DateTimeOffset.UtcNow;
        }
        else
        {
            _snapshot = null;
        }
        return result;
    }

    private async Task<WriteResult> ExecuteCommandAsync(EstunAddressSpace.Command cmd, object value)
    {
        var client = _client!;
        OperateResult op = cmd switch
        {
            EstunAddressSpace.Command.Start => await Task.Run(() => client.RobotStartPrograme()),
            EstunAddressSpace.Command.Stop => await Task.Run(() => client.RobotStopPrograme()),
            EstunAddressSpace.Command.ResetError => await Task.Run(() => client.RobotResetError()),
            EstunAddressSpace.Command.UnregisterProject => await Task.Run(() => client.RobotUnregisterProject()),
            EstunAddressSpace.Command.CommandStatusRestart => await Task.Run(() => client.RobotCommandStatusRestart()),
            EstunAddressSpace.Command.LoadProject => await LoadProjectAsync(client, value),
            EstunAddressSpace.Command.SetGlobalSpeed => await Task.Run(() => client.RobotSetGlobalSpeedValue(Convert.ToInt16(value))),
            _ => new OperateResult($"未支持的指令 {cmd}"),
        };

        // 指令会改变机器人状态，作废缓存以免下一次读取返回过期快照
        _snapshot = null;

        if (!op.IsSuccess) _logger.LogWarning("Estun Robot command {Command} failed: {Error}", cmd, op.Message);
        return new() { Success = op.IsSuccess, ErrorMessage = op.IsSuccess ? null : op.Message };
    }

    private static async Task<OperateResult> LoadProjectAsync(EstunTcpNet client, object value)
    {
        var name = value as string ?? value?.ToString();
        return string.IsNullOrWhiteSpace(name)
            ? new OperateResult("装载工程需要提供非空的工程名")
            : await Task.Run(() => client.RobotLoadProject(name));
    }

    // ---- 原始 Modbus 直传 ----

    private async Task<TagValue> ReadRawAsync(string original, string addr, DataType dataType)
    {
        var c = _client!;
        switch (dataType)
        {
            case DataType.Bool:
                return Wrap(original, dataType, await c.ReadBoolAsync(addr));
            case DataType.Int16:
                return Wrap(original, dataType, await c.ReadInt16Async(addr));
            case DataType.UInt16:
                return Wrap(original, dataType, await c.ReadUInt16Async(addr));
            case DataType.Int32:
                return Wrap(original, dataType, await c.ReadInt32Async(addr));
            case DataType.UInt32:
                return Wrap(original, dataType, await c.ReadUInt32Async(addr));
            case DataType.Int64:
                return Wrap(original, dataType, await c.ReadInt64Async(addr));
            case DataType.Float:
                return Wrap(original, dataType, await c.ReadFloatAsync(addr));
            case DataType.Double:
                return Wrap(original, dataType, await c.ReadDoubleAsync(addr));
            case DataType.String:
                return Wrap(original, dataType, await c.ReadStringAsync(addr, DefaultStringLength, Encoding.ASCII));
            default:
                return Bad(original, dataType, $"埃斯顿机器人不支持读取类型 {dataType}");
        }
    }

    private async Task<WriteResult> WriteRawAsync(string addr, DataType dataType, object value)
    {
        var c = _client!;
        OperateResult op = dataType switch
        {
            DataType.Bool => await c.WriteAsync(addr, Convert.ToBoolean(value)),
            DataType.Int16 => await c.WriteAsync(addr, Convert.ToInt16(value)),
            DataType.UInt16 => await c.WriteAsync(addr, Convert.ToUInt16(value)),
            DataType.Int32 => await c.WriteAsync(addr, Convert.ToInt32(value)),
            DataType.UInt32 => await c.WriteAsync(addr, Convert.ToUInt32(value)),
            DataType.Int64 => await c.WriteAsync(addr, Convert.ToInt64(value)),
            DataType.Float => await c.WriteAsync(addr, Convert.ToSingle(value)),
            DataType.Double => await c.WriteAsync(addr, Convert.ToDouble(value)),
            DataType.String => await c.WriteAsync(addr, value?.ToString() ?? string.Empty, DefaultStringLength, Encoding.ASCII),
            _ => new OperateResult($"埃斯顿机器人不支持写入类型 {dataType}"),
        };

        // 直写寄存器同样可能影响快照内容
        _snapshot = null;

        if (!op.IsSuccess) _logger.LogWarning("Estun Robot raw write error at {Address}: {Error}", addr, op.Message);
        return new() { Success = op.IsSuccess, ErrorMessage = op.IsSuccess ? null : op.Message };
    }

    // ---- 辅助 ----

    private static bool IsSnapshotAddress(string address)
    {
        var a = address.Trim();
        return a.Equals(EstunAddressSpace.SnapshotAddress, StringComparison.OrdinalIgnoreCase)
            || a.StartsWith(EstunAddressSpace.DataPrefix, StringComparison.OrdinalIgnoreCase)
            || IoRegex.IsMatch(a);
    }

    private static TagValue FromBoolArray(string address, DataType dt, bool[]? arr, int idx, string area) =>
        arr is null || idx >= arr.Length
            ? Bad(address, dt, $"{area} 索引 {idx} 越界（快照长度 {arr?.Length ?? 0}）")
            : Ok(address, DataType.Bool, arr[idx]);

    private static TagValue FromFloatArray(string address, DataType dt, float[]? arr, int idx, string area) =>
        arr is null || idx >= arr.Length
            ? Bad(address, dt, $"{area} 索引 {idx} 越界（快照长度 {arr?.Length ?? 0}）")
            : Ok(address, DataType.Float, arr[idx]);

    private static TagValue Wrap<T>(string address, DataType dt, OperateResult<T> r) =>
        r.IsSuccess && r.Content is not null ? Ok(address, dt, r.Content) : Bad(address, dt, r.Message);

    private static bool TryGetByte(DeviceConnectionConfig config, string key, out byte value) =>
        byte.TryParse(config.ExtendedProperties.GetValueOrDefault(key), out value);

    private static bool TryGetPositiveInt(DeviceConnectionConfig config, string key, out int value) =>
        int.TryParse(config.ExtendedProperties.GetValueOrDefault(key), out value) && value > 0;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>
    /// 停掉 <see cref="EstunTcpNet"/> 构造函数里私自启动的保活定时器。
    ///
    /// Hsl 的 EstunTcpNet 构造函数里有 <c>new Timer(ThreadTimerTick, null, 3000, 10000)</c>，
    /// 每 10 秒在同一个客户端上读一次 "0" 保活。该 Timer 是私有字段，<c>ConnectClose()</c> 不会停它，
    /// 也没有公开的关闭入口 —— 客户端对象被弃用后它仍在后台每 10 秒尝试重连并读取，
    /// 每次失败都会占满 ConnectTimeOut 的线程池线程。驱动实例反复创建时会累积成线程池饥饿。
    ///
    /// 本驱动的存活探测由 <see cref="PingAsync"/> 和连接池健康检查负责，不需要这个定时器。
    /// </summary>
    private void DisableHslKeepAliveTimer(EstunTcpNet client)
    {
        try
        {
            var field = typeof(EstunTcpNet).GetField("timer", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(client) is IDisposable timer)
            {
                timer.Dispose();
                return;
            }
            _logger.LogDebug("EstunTcpNet 未找到预期的保活定时器字段，跳过（Hsl 实现可能已变更）");
        }
        catch (Exception ex)
        {
            // 拿不到就算了 —— 退化为 Hsl 原生行为，不影响功能正确性
            _logger.LogDebug(ex, "关闭 EstunTcpNet 内置保活定时器失败");
        }
    }

    private void Cleanup()
    {
        try { _client?.ConnectClose(); } catch { }
        _client = null;
        _snapshot = null;
        _snapshotAt = DateTimeOffset.MinValue;
    }

    private void SetState(ConnectionState next, string? reason = null)
    {
        var old = _state;
        if (old == next) return;
        _state = next;
        StateChanged?.Invoke(this, new() { OldState = old, NewState = next, Reason = reason });
    }

    private static TagValue Ok(string address, DataType dt, object value) => new()
    {
        Address = address, DataType = dt, Value = value,
        Quality = TagQuality.Good, Timestamp = DateTimeOffset.UtcNow,
    };

    private static TagValue Bad(string address, DataType dt, string? err) => new()
    {
        Address = address, DataType = dt, Value = DefaultOf(dt),
        Quality = TagQuality.Bad, Timestamp = DateTimeOffset.UtcNow, ErrorMessage = err,
    };

    private static object DefaultOf(DataType dt) => dt switch
    {
        DataType.Bool => false,
        DataType.Int16 => (short)0, DataType.UInt16 => (ushort)0,
        DataType.Int32 => 0, DataType.UInt32 => 0u,
        DataType.Float => 0f, DataType.Double => 0d,
        DataType.Int64 => 0L, DataType.String => string.Empty,
        DataType.ByteArray => Array.Empty<byte>(),
        _ => 0,
    };
}
