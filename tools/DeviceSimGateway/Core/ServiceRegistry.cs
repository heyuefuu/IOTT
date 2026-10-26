namespace DeviceSimGateway.Core;

using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>服务注册与配置持久化；提供启动/停止/查询与实时数据快照。</summary>
public sealed class ServiceRegistry : IDisposable
{
    private readonly List<IDeviceService> _services;
    private readonly Dictionary<string, SimServiceConfig> _configs = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _configPath;
    private readonly ILogger<ServiceRegistry> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public SimDataStore Store { get; }
    public DataGenerator Generator { get; }
    public ThirdPartySubscriber ThirdParty { get; }

    public ServiceRegistry(IEnumerable<IDeviceService> services, string configPath, ILogger<ServiceRegistry> log, ILoggerFactory loggerFactory)
    {
        _services = services.ToList();
        _configPath = configPath;
        _log = log;
        ThirdParty = new ThirdPartySubscriber(loggerFactory.CreateLogger<ThirdPartySubscriber>());

        var cfg = LoadConfig();
        foreach (var svc in _services)
        {
            var existing = cfg.Services.FirstOrDefault(s => s.Key.Equals(svc.Key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new SimServiceConfig { Key = svc.Key, Port = svc.DefaultPort, Enabled = svc.Implemented && svc.Key is "modbus" or "mtconnect" };
                cfg.Services.Add(existing);
            }
            if (existing.Port <= 0) existing.Port = svc.DefaultPort;
            _configs[svc.Key] = existing;
        }
        Store = new SimDataStore(cfg.Generator);
        Store.Failure.Apply(cfg.FailureEnabled, cfg.FailureRatePercent, cfg.FailureDropConnection);
        ThirdParty.Apply(cfg.ThirdParty);
        Generator = new DataGenerator(Store, cfg.Generator);
        Generator.SeedHolding();
        SaveConfig();
    }

    private GatewayConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
                return JsonSerializer.Deserialize<GatewayConfig>(File.ReadAllText(_configPath), JsonOpts) ?? new GatewayConfig();
        }
        catch (Exception ex) { _log.LogWarning(ex, "读取 sim-config.json 失败，使用默认配置"); }
        return new GatewayConfig();
    }

    public void SaveConfig()
    {
        var failure = Store.Failure.Snapshot();
        var cfg = new GatewayConfig
        {
            Services = _configs.Values.ToList(),
            Generator = Generator.Config,
            FailureEnabled = failure.Enabled,
            FailureRatePercent = failure.RatePercent,
            FailureDropConnection = failure.DropConnection,
            ThirdParty = ThirdParty.Config,
        };
        try { File.WriteAllText(_configPath, JsonSerializer.Serialize(cfg, JsonOpts)); }
        catch (Exception ex) { _log.LogWarning(ex, "写入 sim-config.json 失败"); }
    }

    /// <summary>应用随机失败注入策略并持久化。</summary>
    public void SetFailure(bool enabled, double ratePercent, bool dropConnection)
    {
        Store.Failure.Apply(enabled, ratePercent, dropConnection);
        SaveConfig();
    }

    public IReadOnlyList<ServiceRuntime> GetInfos() =>
        _services.Select(s =>
        {
            var c = _configs[s.Key];
            var state = s.Implemented ? s.State : ServiceState.NotImplemented;
            var msg = s.Implemented ? s.Message : "未实现（扩展占位）";
            return new ServiceRuntime(s.Key, s.Name, s.Description, s.Implemented, s.Port > 0 ? s.Port : c.Port, c.Enabled, state, msg);
        }).ToList();

    public async Task StartAllAsync(CancellationToken ct)
    {
        Generator.Start();
        foreach (var svc in _services)
        {
            var c = _configs[svc.Key];
            if (c.Enabled && svc.Implemented) await SafeStartAsync(svc, c.Port, ct);
        }
    }

    public async Task<string> StartAsync(string key, CancellationToken ct)
    {
        var svc = _services.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (svc is null) return $"未找到服务 {key}";
        if (!svc.Implemented) return $"{svc.Name} 未实现。";
        var c = _configs[svc.Key];
        c.Enabled = true; SaveConfig();
        await SafeStartAsync(svc, c.Port, ct);
        return $"{svc.Name} 已启动（端口 {c.Port}），状态 {svc.State}";
    }

    public async Task<string> StopAsync(string key, CancellationToken ct)
    {
        var svc = _services.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (svc is null) return $"未找到服务 {key}";
        var c = _configs[svc.Key];
        c.Enabled = false; SaveConfig();
        await svc.StopAsync(ct);
        return $"{svc.Name} 已停止";
    }

    /// <summary>更新端口/启用状态；若在运行则按新端口重启。</summary>
    public async Task<string> SetConfigAsync(string key, int? port, bool? enabled, CancellationToken ct)
    {
        var svc = _services.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (svc is null) return $"未找到服务 {key}";
        var c = _configs[svc.Key];
        bool restart = false;
        if (port is int p && p > 0 && p <= 65535 && p != c.Port) { c.Port = p; restart = svc.State == ServiceState.Running; }
        if (enabled is bool e) c.Enabled = e;
        SaveConfig();
        if (restart)
        {
            await svc.StopAsync(ct);
            if (c.Enabled && svc.Implemented) await SafeStartAsync(svc, c.Port, ct);
        }
        return $"{svc.Name} 配置已更新（端口 {c.Port}，启用 {c.Enabled}）";
    }

    public void SetGenerator(GeneratorConfig cfg)
    {
        Generator.Apply(cfg);
        SaveConfig();
    }

    /// <summary>应用"虚拟第三方订阅"配置并持久化。</summary>
    public void SetThirdParty(ThirdPartySubscriberConfig cfg)
    {
        ThirdParty.Apply(cfg);
        SaveConfig();
    }

    private async Task SafeStartAsync(IDeviceService svc, int port, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await svc.StopAsync(ct);
            await svc.StartAsync(port, Store, Generator.Config, ct);
            _log.LogInformation("服务 {Key} 已启动，端口 {Port}，状态 {State}", svc.Key, port, svc.State);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "启动服务 {Key} 失败", svc.Key);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAllAsync(CancellationToken ct)
    {
        Generator.Stop();
        foreach (var svc in _services) { try { await svc.StopAsync(ct); } catch { } }
    }

    public void Dispose() { try { Generator.Dispose(); } catch { } }
}
