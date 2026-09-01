namespace IndustrialIoT.Infrastructure.BackgroundServices;

using System.Collections.Concurrent;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public interface IDeviceConnectionPool
{
    Task<IProtocolDriver> GetOrCreateAsync(string deviceId, CancellationToken ct = default);
    Task ReleaseAsync(string deviceId, CancellationToken ct = default);

    /// <summary>Returns the pooled driver only if one is already connected — never opens a new connection.</summary>
    bool TryGetConnected(string deviceId, out IProtocolDriver? driver);

    int ActiveConnections { get; }
}

public class ConnectionPoolService : BackgroundService, IDeviceConnectionPool
{
    private readonly ConcurrentDictionary<string, PoolEntry> _pool = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceLocks = new();
    private readonly SemaphoreSlim _globalLimit = new(150); // max connections
    private readonly IProtocolDriverFactory _factory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConnectionPoolService> _logger;

    public int ActiveConnections => _pool.Count(kv => kv.Value.Driver.State == ConnectionState.Connected);

    public ConnectionPoolService(IProtocolDriverFactory factory, IServiceProvider serviceProvider, ILogger<ConnectionPoolService> logger)
    {
        _factory = factory;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<IProtocolDriver> GetOrCreateAsync(string deviceId, CancellationToken ct = default)
    {
        if (_pool.TryGetValue(deviceId, out var entry) && entry.Driver.State == ConnectionState.Connected)
            return entry.Driver;

        // Serialize per device so concurrent callers cannot open duplicate connections
        var gate = _deviceLocks.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_pool.TryGetValue(deviceId, out entry))
            {
                if (entry.Driver.State == ConnectionState.Connected)
                    return entry.Driver;

                // Stale entry (faulted / disconnected) — drop it and free its permit
                await ReleaseAsync(deviceId, ct);
            }

            await _globalLimit.WaitAsync(ct);
            IProtocolDriver? driver = null;
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
                var device = await repo.GetByIdAsync(deviceId, ct)
                    ?? throw new InvalidOperationException($"Device {deviceId} not found");

                driver = _factory.Create(device.Protocol, device.Brand, device.Model);
                var result = await driver.ConnectAsync(device.ConnectionConfig, ct);
                if (!result.Success)
                    throw new InvalidOperationException($"Failed to connect to device {deviceId}: {result.ErrorMessage}");

                _pool[deviceId] = new PoolEntry { Driver = driver, DeviceId = deviceId, ConnectedAt = DateTimeOffset.UtcNow };
                _logger.LogInformation("Opened pooled connection for device {DeviceId}", deviceId);
                return driver;
            }
            catch
            {
                if (driver is not null)
                {
                    try { await driver.DisposeAsync(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Error disposing failed driver for device {DeviceId}", deviceId); }
                }
                _globalLimit.Release();
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ReleaseAsync(string deviceId, CancellationToken ct = default)
    {
        if (_pool.TryRemove(deviceId, out var entry))
        {
            try
            {
                await entry.Driver.DisconnectAsync(ct);
                await entry.Driver.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing pooled connection for device {DeviceId}", deviceId);
            }
            finally
            {
                _globalLimit.Release();
            }
        }
    }

    public bool TryGetConnected(string deviceId, out IProtocolDriver? driver)
    {
        if (_pool.TryGetValue(deviceId, out var entry) && entry.Driver.State == ConnectionState.Connected)
        {
            driver = entry.Driver;
            return true;
        }
        driver = null;
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ConnectionPoolService started");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var (id, entry) in _pool)
            {
                try
                {
                    if (!await entry.Driver.PingAsync(stoppingToken))
                    {
                        _logger.LogWarning("Device {DeviceId} ping failed, removing from pool", id);
                        await ReleaseAsync(id, stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Health check failed for device {DeviceId}", id);
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // Close every pooled connection gracefully instead of letting the process drop the sockets
        foreach (var id in _pool.Keys.ToList())
            await ReleaseAsync(id, CancellationToken.None);
    }

    private record PoolEntry
    {
        public required IProtocolDriver Driver { get; init; }
        public required string DeviceId { get; init; }
        public required DateTimeOffset ConnectedAt { get; init; }
    }
}
