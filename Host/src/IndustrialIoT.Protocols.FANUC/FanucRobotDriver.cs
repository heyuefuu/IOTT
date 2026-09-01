namespace IndustrialIoT.Protocols.FANUC;

using System.Text;
using System.Text.RegularExpressions;
using HslCommunication;
using HslCommunication.Robot.FANUC;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

/// <summary>
/// FANUC 机器人驱动（R-30iB / CRX / M / R / LR 系列），基于 HslCommunication FanucInterfaceNet。
/// 地址语法：
///   布尔 IO:  SDO1、SDI1、RDO1、RDI1、SO0、SI0、UO1、UI1、WO1(=SDO8000+n)、WI1(=SDI8000+n)
///   字 IO:   GO1、GI1、AO1(=GO1000+n)、AI1(=GI1000+n)
///   状态数据: DATA:&lt;FanucData属性名&gt;，如 DATA:CurrentPosition
///   整机快照: FANUC_DATA  (返回 JSON 字符串)
/// </summary>
[ProtocolDriver(ProtocolType.FanucRobot, "FANUC", "发那科", "Robot", "机器人", "CRX", "M-", "R-", "LR")]
public sealed class FanucRobotDriver : IProtocolDriver, IAddressSpaceBrowser
{
    private static readonly Regex IoRegex = new(
        @"^(?<area>SDO|SDI|RDO|RDI|SO|SI|UO|UI|WO|WI|GO|GI|AO|AI)(?<idx>\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// CRX / R-30iB Plus 协作机器人 UOP 标准信号语义化别名（FANUC 控制器规范，跨型号一致）。
    /// 输入 (UI*) 可写、控制器从外部接收；输出 (UO*) 只读、控制器报告状态。
    /// 地址解析时优先匹配此映射，未命中再走 IoRegex / DATA: / FANUC_DATA。
    /// </summary>
    private static readonly Dictionary<string, string> CrxAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CRX/EmergencyStop"] = "UI1", ["CRX/Hold"] = "UI2", ["CRX/SafetyFence"] = "UI3",
        ["CRX/CycleStop"] = "UI4",     ["CRX/Reset"] = "UI5", ["CRX/Start"] = "UI6",
        ["CRX/Enable"] = "UI8",
        ["CRX/CmdEnabled"] = "UO1",    ["CRX/SystemReady"] = "UO2", ["CRX/ProgRunning"] = "UO3",
        ["CRX/ProgPaused"] = "UO4",    ["CRX/Fault"] = "UO6",       ["CRX/AtPerch"] = "UO7",
        ["CRX/TpEnabled"] = "UO8",     ["CRX/Battery"] = "UO9",     ["CRX/Busy"] = "UO10",
    };

    private readonly ILogger<FanucRobotDriver> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private FanucInterfaceNet? _client;
    private ConnectionState _state = ConnectionState.Disconnected;
    private TimeSpan _pingTimeout = TimeSpan.FromMilliseconds(3000);

    public ProtocolType Protocol => ProtocolType.FanucRobot;
    public ConnectionState State => _state;

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Read | DriverCapabilities.Write |
        DriverCapabilities.Browse | DriverCapabilities.BatchRead;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public FanucRobotDriver(ILogger<FanucRobotDriver> logger) => _logger = logger;

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        if (_state == ConnectionState.Connected) return new() { Success = true };
        SetState(ConnectionState.Connecting);

        try
        {
            var client = new FanucInterfaceNet(config.Host, config.Port == 0 ? 60008 : config.Port)
            {
                ConnectTimeOut = (int)config.ConnectTimeout.TotalMilliseconds,
                ReceiveTimeOut = (int)config.ReadTimeout.TotalMilliseconds,
                StringEncoding = ResolveEncoding(config),
            };

            var r = await client.ConnectServerAsync();
            if (!r.IsSuccess) throw new InvalidOperationException(r.Message);

            if (config.ExtendedProperties.TryGetValue("PingTimeout", out var ptStr) && int.TryParse(ptStr, out var pt) && pt > 0)
                _pingTimeout = TimeSpan.FromMilliseconds(pt);

            _client?.ConnectClose();
            _client = client;

            _logger.LogInformation("FANUC Robot connected to {Host}:{Port}", config.Host, client.Port);
            SetState(ConnectionState.Connected);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            Cleanup();
            _logger.LogError(ex, "FANUC Robot connection failed");
            SetState(ConnectionState.Faulted, ex.Message);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        try { _client?.ConnectClose(); } catch { }
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
            if (!acquired) return false;
            var readTask = _client.ReadFanucDataAsync();
            var winner = await Task.WhenAny(readTask, Task.Delay(_pingTimeout, ct));
            if (winner != readTask) return false;
            return readTask.Result.IsSuccess;
        }
        catch { return false; }
        finally { if (acquired) _semaphore.Release(); }
    }

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            return await ReadCoreAsync(address, dataType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "FANUC Robot read failed at {Address}", address);
            return BadTag(address, dataType, ex.Message);
        }
        finally { _semaphore.Release(); }
    }

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(
        IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        var list = new List<TagValue>(requests.Count);
        foreach (var req in requests) list.Add(await ReadTagAsync(req.Address, req.DataType, ct));
        return list;
    }

    public async Task<WriteResult> WriteTagAsync(
        string address, DataType dataType, object value, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            var resolved = CrxAlias.TryGetValue(address.Trim(), out var alias) ? alias : address.Trim();
            var m = IoRegex.Match(resolved);
            if (!m.Success)
                return new() { Success = false, ErrorMessage = $"FANUC 机器人不支持写入地址 '{address}'" };

            var area = m.Groups["area"].Value.ToUpperInvariant();
            ushort idx = ushort.Parse(m.Groups["idx"].Value);
            var client = _client!;

            OperateResult result = area switch
            {
                "SDO" => await client.WriteSDOAsync(idx, new[] { Convert.ToBoolean(value) }),
                "SDI" => await client.WriteSDIAsync(idx, new[] { Convert.ToBoolean(value) }),
                "RDO" => await client.WriteRDOAsync(idx, new[] { Convert.ToBoolean(value) }),
                "RDI" => await client.WriteRDIAsync(idx, new[] { Convert.ToBoolean(value) }),
                "WO"  => await client.WriteSDOAsync((ushort)(8000 + idx), new[] { Convert.ToBoolean(value) }),
                "WI"  => await client.WriteSDIAsync((ushort)(8000 + idx), new[] { Convert.ToBoolean(value) }),
                "GO"  => await client.WriteGOAsync(idx, new[] { Convert.ToUInt16(value) }),
                "GI"  => await client.WriteGIAsync(idx, new[] { Convert.ToUInt16(value) }),
                "AO"  => await client.WriteGOAsync((ushort)(1000 + idx), new[] { Convert.ToUInt16(value) }),
                "AI"  => await client.WriteGIAsync((ushort)(1000 + idx), new[] { Convert.ToUInt16(value) }),
                _ => new OperateResult($"区域 {area} 不可写"),
            };

            if (!result.IsSuccess)
            {
                _logger.LogWarning("FANUC Robot write error at {Address}: {Error}", address, result.Message);
                return new() { Success = false, ErrorMessage = result.Message };
            }
            return new() { Success = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "FANUC Robot write failed at {Address}", address);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
        finally { _semaphore.Release(); }
    }

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        IReadOnlyList<AddressNode> nodes;
        if (string.IsNullOrEmpty(parentPath))
        {
            nodes = new[]
            {
                Folder("IO", "数字/组 IO"),
                Folder("DATA", "状态数据 (FanucData 属性)"),
                Folder("CRX", "CRX 协作机器人 UOP 信号别名"),
                Variable("FANUC_DATA", "整机状态快照 (JSON)", DataType.String, writable: false),
            };
        }
        else if (parentPath.Equals("CRX", StringComparison.OrdinalIgnoreCase))
        {
            nodes = CrxAlias
                .Select(kv => Variable(
                    kv.Key,
                    $"{kv.Key.Substring(4)} → {kv.Value}",
                    DataType.Bool,
                    writable: kv.Value.StartsWith("UI", StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
        else if (parentPath.Equals("IO", StringComparison.OrdinalIgnoreCase))
        {
            nodes = new[]
            {
                Folder("IO/SDO", "系统数字输出"), Folder("IO/SDI", "系统数字输入"),
                Folder("IO/RDO", "机器人数字输出"), Folder("IO/RDI", "机器人数字输入"),
                Folder("IO/SO",  "系统输出"),       Folder("IO/SI",  "系统输入"),
                Folder("IO/UO",  "UOP 输出"),       Folder("IO/UI",  "UOP 输入"),
                Folder("IO/WO",  "焊接输出"),       Folder("IO/WI",  "焊接输入"),
                Folder("IO/GO",  "组输出 UInt16"),  Folder("IO/GI",  "组输入 UInt16"),
                Folder("IO/AO",  "模拟输出 UInt16"),Folder("IO/AI",  "模拟输入 UInt16"),
            };
        }
        else if (parentPath.StartsWith("IO/", StringComparison.OrdinalIgnoreCase))
        {
            var area = parentPath[3..].ToUpperInvariant();
            var isWord = area is "GO" or "GI" or "AO" or "AI";
            var isWritable = area is not ("SO" or "SI" or "UO" or "UI");
            int start = area is "SO" or "SI" ? 0 : 1;
            var list = new List<AddressNode>(32);
            for (int i = start; i < start + 32; i++)
                list.Add(Variable($"{area}{i}", $"{area}{i}", isWord ? DataType.UInt16 : DataType.Bool, writable: isWritable));
            nodes = list;
        }
        else if (parentPath.Equals("DATA", StringComparison.OrdinalIgnoreCase))
        {
            nodes = typeof(FanucData).GetProperties()
                .Select(p => Variable($"DATA:{p.Name}", p.Name, DataType.String, writable: false))
                .ToArray();
        }
        else
        {
            nodes = Array.Empty<AddressNode>();
        }
        return Task.FromResult(nodes);
    }

    public Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Path,DisplayName,DataType,Readable,Writable");
        string[] boolAreas = { "SDO", "SDI", "RDO", "RDI", "SO", "SI", "UO", "UI", "WO", "WI" };
        string[] wordAreas = { "GO", "GI", "AO", "AI" };
        foreach (var a in boolAreas)
        {
            int start = a is "SO" or "SI" ? 0 : 1;
            for (int i = start; i < start + 32; i++)
                sb.AppendLine($"{a}{i},{a}{i},Bool,True,{(a is "SO" or "SI" or "UO" or "UI" ? "False" : "True")}");
        }
        foreach (var a in wordAreas)
            for (int i = 1; i <= 16; i++)
                sb.AppendLine($"{a}{i},{a}{i},UInt16,True,True");
        foreach (var p in typeof(FanucData).GetProperties())
            sb.AppendLine($"DATA:{p.Name},{p.Name},String,True,False");
        sb.AppendLine("FANUC_DATA,整机快照,String,True,False");

        Stream s = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        return Task.FromResult(s);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<TagValue> ReadCoreAsync(string address, DataType dataType)
    {
        var client = _client!;
        var normalized = address.Trim();
        if (CrxAlias.TryGetValue(normalized, out var aliased)) normalized = aliased;

        if (normalized.Equals("FANUC_DATA", StringComparison.OrdinalIgnoreCase))
        {
            var r = await client.ReadFanucDataAsync();
            return r.IsSuccess
                ? Ok(address, DataType.String, Newtonsoft.Json.Linq.JObject.FromObject(r.Content).ToString())
                : BadTag(address, dataType, r.Message);
        }

        if (normalized.StartsWith("DATA:", StringComparison.OrdinalIgnoreCase))
        {
            var prop = normalized[5..];
            var r = client.ReadString(prop);
            return r.IsSuccess ? Ok(address, DataType.String, r.Content) : BadTag(address, dataType, r.Message);
        }

        var m = IoRegex.Match(normalized);
        if (!m.Success) return BadTag(address, dataType, $"无效的 FANUC 机器人地址 '{address}'");

        var area = m.Groups["area"].Value.ToUpperInvariant();
        ushort idx = ushort.Parse(m.Groups["idx"].Value);

        OperateResult<bool[]>? br = area switch
        {
            "SDO" => await client.ReadSDOAsync(idx, 1),
            "SDI" => await client.ReadSDIAsync(idx, 1),
            "RDO" => await client.ReadRDOAsync(idx, 1),
            "RDI" => await client.ReadRDIAsync(idx, 1),
            "SO"  => await client.ReadSOAsync(idx, 1),
            "SI"  => await client.ReadSIAsync(idx, 1),
            "UO"  => await client.ReadUOAsync(idx, 1),
            "UI"  => await client.ReadUIAsync(idx, 1),
            "WO"  => await client.ReadSDOAsync((ushort)(8000 + idx), 1),
            "WI"  => await client.ReadSDIAsync((ushort)(8000 + idx), 1),
            _ => null,
        };
        if (br is not null)
            return br.IsSuccess
                ? Ok(address, dataType, dataType == DataType.Bool ? br.Content[0] : (object)br.Content)
                : BadTag(address, dataType, br.Message);

        OperateResult<ushort[]> wr = area switch
        {
            "GO" => await client.ReadGOAsync(idx, 1),
            "GI" => await client.ReadGIAsync(idx, 1),
            "AO" => await client.ReadGOAsync((ushort)(1000 + idx), 1),
            "AI" => await client.ReadGIAsync((ushort)(1000 + idx), 1),
            _ => new OperateResult<ushort[]>($"未知区域 {area}"),
        };
        return wr.IsSuccess
            ? Ok(address, dataType, Convert.ChangeType(wr.Content[0], TypeForWord(dataType)))
            : BadTag(address, dataType, wr.Message);
    }

    private static Type TypeForWord(DataType dt) => dt switch
    {
        DataType.Int16 => typeof(short),
        DataType.UInt16 => typeof(ushort),
        DataType.Int32 => typeof(int),
        DataType.UInt32 => typeof(uint),
        _ => typeof(ushort),
    };

    private static Encoding ResolveEncoding(DeviceConnectionConfig config)
    {
        if (config.ExtendedProperties.TryGetValue("Encoding", out var name) && !string.IsNullOrWhiteSpace(name))
        {
            try { return Encoding.GetEncoding(name); } catch { }
        }
        return Encoding.UTF8;
    }

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected || _client is null)
            throw new InvalidOperationException("FANUC 机器人驱动未连接");
    }

    private void SetState(ConnectionState s, string? reason = null)
    {
        var old = _state;
        if (old == s) return;
        _state = s;
        StateChanged?.Invoke(this, new() { OldState = old, NewState = s, Reason = reason });
    }

    private void Cleanup()
    {
        try { _client?.ConnectClose(); } catch { }
        _client = null;
    }

    private static TagValue Ok(string address, DataType dt, object value) => new()
    {
        Address = address, DataType = dt, Value = value,
        Quality = TagQuality.Good, Timestamp = DateTimeOffset.UtcNow,
    };

    private static TagValue BadTag(string address, DataType dt, string? err) => new()
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

    private static AddressNode Folder(string path, string name) => new()
    {
        Path = path, DisplayName = name, NodeType = AddressNodeType.Folder,
        IsReadable = true, IsWritable = false,
    };

    private static AddressNode Variable(string path, string name, DataType dt, bool writable = true) => new()
    {
        Path = path, DisplayName = name, NodeType = AddressNodeType.Variable,
        DataType = dt, IsReadable = true, IsWritable = writable,
    };
}
