using System.Collections.Concurrent;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

public interface ICsConnectivityService
{
    IReadOnlyList<CsGateway> ListGateways();
    CsGateway UpsertGateway(CsGateway gateway);
    bool DeleteGateway(string id);
    Task<CsProbeResult> ProbeGatewayAsync(string id, CancellationToken ct);

    IReadOnlyList<CsDataSource> ListDataSources();
    CsDataSource UpsertDataSource(CsDataSource dataSource);
    bool DeleteDataSource(string id);
    bool EnableDataSource(string id);
    bool DisableDataSource(string id);

    IReadOnlyList<CsServerService> ListServers();
    CsServerService UpsertServer(CsServerService server);
    bool DeleteServer(string id);
    Task<bool> StartServerAsync(string id);
    bool StopServer(string id);
    IReadOnlyList<CsServerConnection> GetServerConnections(string id);

    Task<CsProbeResult> ProbeTcpAsync(string host, int port, int timeoutMs, CancellationToken ct);
    Task<CsParallelTestResult> RunParallelTestAsync(CsParallelTestRequest request, CancellationToken ct);
    /// <param name="gateWait">压测互斥闸的最长等待时间；null 表示不等待，闸被占用时直接抛 <see cref="CsParallelTestBusyException"/>。</param>
    Task<CsParallelTestResult> RunSameTargetParallelTestAsync(CsSameTargetParallelTestRequest request, CancellationToken ct, TimeSpan? gateWait = null);
}

/// <summary>并发压测互斥闸被占用（同一时间仅允许一个压测在跑）。</summary>
public sealed class CsParallelTestBusyException : InvalidOperationException
{
    public CsParallelTestBusyException() : base("已有并发压测正在运行，请稍后重试") { }
}

/// <summary>
/// C/S 通讯验证核心引擎：基于 System.Net.Sockets 的真实 TcpListener/TcpClient。
/// 配置持久化到 App_Data，Socket 监听、探测循环等运行态仍仅保存在进程内。
/// </summary>
public sealed partial class CsConnectivityService : ICsConnectivityService, IDisposable
{
    private readonly ILogger<CsConnectivityService> _logger;
    private readonly object _configurationGate = new();
    private readonly string _configurationPath;
    private readonly SemaphoreSlim _parallelTestGate = new(1, 1);
    private readonly object _ftpStorageGate = new();
    private readonly SemaphoreSlim _ftpUploadSlots = new(4, 4);
    private long _ftpStoredBytes;
    private readonly ConcurrentDictionary<string, CsGateway> _gateways = new();
    private readonly ConcurrentDictionary<string, CsDataSource> _dataSources = new();
    private readonly ConcurrentDictionary<string, CsServerService> _servers = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _dataSourceLoops = new();
    private readonly ConcurrentDictionary<string, ServerRuntime> _serverRuntimes = new();

    public CsConnectivityService(ILogger<CsConnectivityService> logger)
        : this(logger, null)
    {
    }

    public CsConnectivityService(ILogger<CsConnectivityService> logger, string? configurationPath)
    {
        _logger = logger;
        _configurationPath = configurationPath ?? Path.Combine(
            AppContext.BaseDirectory, "App_Data", "cs-connectivity.json");
        LoadConfiguration();
    }

    internal static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];
    internal static bool IsFtp(string? type) => string.Equals(type, "FTP", StringComparison.OrdinalIgnoreCase);
    internal static bool IsMqttGateway(string? type) => string.Equals(type, "MQTT", StringComparison.OrdinalIgnoreCase);

    /// <summary>按网关类型分派探测：FTP 真实登录、MQTT 真实 CONNECT/CONNACK，其余为 TCP 建连验证。</summary>
    private Task<CsProbeResult> ProbeGatewayByTypeAsync(CsGateway gw, bool ftpFullRoundTrip, int timeoutMs, CancellationToken ct)
    {
        if (IsFtp(gw.Type)) return ProbeFtpClientAsync(gw, ftpFullRoundTrip, ct);
        if (IsMqttGateway(gw.Type))
            return ProbeMqttCoreAsync(gw.Ip, gw.Port, timeoutMs, useTls: false,
                gw.Username, gw.Password, clientId: null, ct);
        return ProbeTcpAsync(gw.Ip, gw.Port, timeoutMs, ct);
    }

    // ---- 网关（客户端连接目标）----
    public IReadOnlyList<CsGateway> ListGateways() => _gateways.Values.OrderBy(g => g.Name).ToList();

    public CsGateway UpsertGateway(CsGateway gateway)
    {
        lock (_configurationGate)
        {
            if (string.IsNullOrWhiteSpace(gateway.Id)) gateway.Id = NewId("gw");
            if (_gateways.TryGetValue(gateway.Id, out var current) &&
                string.IsNullOrWhiteSpace(gateway.Password))
                gateway.Password = current.Password;
            _gateways[gateway.Id] = gateway;
            SaveConfigurationLocked();
            return gateway;
        }
    }

    public bool DeleteGateway(string id)
    {
        // 关联数据源的停用与网关删除同锁完成，避免与 Upsert 并发时漏停刚加入的探测循环
        lock (_configurationGate)
        {
            foreach (var ds in _dataSources.Values.Where(d => d.GatewayId == id).ToList())
            {
                StopDataSourceLoopLocked(ds.Id);
                ds.Status = "禁用";
            }
            var removed = _gateways.TryRemove(id, out _);
            if (removed) SaveConfigurationLocked();
            return removed;
        }
    }

    public async Task<CsProbeResult> ProbeGatewayAsync(string id, CancellationToken ct)
    {
        if (!_gateways.TryGetValue(id, out var gw))
            return new CsProbeResult(false, 0, "网关不存在", Now());
        var result = await ProbeGatewayByTypeAsync(gw, ftpFullRoundTrip: true, timeoutMs: 3000, ct);
        gw.Status = result.Success ? "运行中" : "停止";
        if (result.Success) gw.LastHeartbeat = result.Timestamp;
        return result;
    }

    // ---- 客户端数据源（周期探测）----
    public IReadOnlyList<CsDataSource> ListDataSources() => _dataSources.Values.OrderBy(d => d.Name).ToList();

    public CsDataSource UpsertDataSource(CsDataSource dataSource)
    {
        lock (_configurationGate)
        {
            if (string.IsNullOrWhiteSpace(dataSource.Id)) dataSource.Id = NewId("ds");
            var shouldEnable = _dataSourceLoops.ContainsKey(dataSource.Id) ||
                string.Equals(dataSource.Status, "启用", StringComparison.Ordinal);
            StopDataSourceLoopLocked(dataSource.Id);
            dataSource.Status = "禁用";
            _dataSources[dataSource.Id] = dataSource;
            if (shouldEnable) StartDataSourceLoopLocked(dataSource);
            SaveConfigurationLocked();
            return dataSource;
        }
    }

    public bool DeleteDataSource(string id)
    {
        lock (_configurationGate)
        {
            StopDataSourceLoopLocked(id);
            var removed = _dataSources.TryRemove(id, out _);
            if (removed) SaveConfigurationLocked();
            return removed;
        }
    }

    public bool EnableDataSource(string id)
    {
        lock (_configurationGate)
        {
            if (!_dataSources.TryGetValue(id, out var ds)) return false;
            StopDataSourceLoopLocked(id);
            StartDataSourceLoopLocked(ds);
            return true;
        }
    }

    public bool DisableDataSource(string id)
    {
        lock (_configurationGate)
        {
            StopDataSourceLoopLocked(id);
            if (_dataSources.TryGetValue(id, out var ds)) ds.Status = "禁用";
            return true;
        }
    }

    private void StartDataSourceLoopLocked(CsDataSource dataSource)
    {
        dataSource.Status = "启用";
        var cts = new CancellationTokenSource();
        _dataSourceLoops[dataSource.Id] = cts;
        _ = ProbeLoopAsync(dataSource, cts.Token);
    }

    private void StopDataSourceLoopLocked(string id)
    {
        if (_dataSourceLoops.TryRemove(id, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var id in _dataSourceLoops.Keys.ToList()) DisableDataSource(id);
        StopAllServers();
    }
}
