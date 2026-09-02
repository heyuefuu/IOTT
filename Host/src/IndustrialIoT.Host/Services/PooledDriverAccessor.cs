namespace IndustrialIoT.Host.Services;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Infrastructure.BackgroundServices;
using IndustrialIoT.Protocols.Abstractions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Borrows a long-lived pooled driver for a single request.
/// <para>
/// Manual read/write/browse endpoints used to create a driver per request and dispose it on the way
/// out, which meant one TCP connect + one frame + an immediate client-side close per UI click. Real
/// controllers (e.g. Estun robots) log that as a connection fault ([53011] 外部客户端主动断开连接).
/// Reusing the pooled connection keeps the socket open between operations.
/// </para>
/// </summary>
public interface IPooledDriverAccessor
{
    /// <summary>
    /// Runs <paramref name="action"/> against the device's pooled driver.
    /// If the driver faults, the pool entry is evicted so the next call reconnects.
    /// </summary>
    Task<PooledDriverResult<T>> ExecuteAsync<T>(
        string deviceId,
        Func<IProtocolDriver, Task<T>> action,
        CancellationToken ct = default);
}

public readonly record struct PooledDriverResult<T>(bool Success, T? Value, string? ErrorMessage);

public sealed class PooledDriverAccessor : IPooledDriverAccessor
{
    private readonly IDeviceConnectionPool _pool;
    private readonly ILogger<PooledDriverAccessor> _logger;

    public PooledDriverAccessor(IDeviceConnectionPool pool, ILogger<PooledDriverAccessor> logger)
    {
        _pool = pool;
        _logger = logger;
    }

    public async Task<PooledDriverResult<T>> ExecuteAsync<T>(
        string deviceId,
        Func<IProtocolDriver, Task<T>> action,
        CancellationToken ct = default)
    {
        IProtocolDriver driver;
        try
        {
            driver = await _pool.GetOrCreateAsync(deviceId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not obtain a pooled connection for device {DeviceId}", deviceId);
            return new(false, default, ex.Message);
        }

        try
        {
            return new(true, await action(driver), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Only tear the pooled connection down when the connection itself is gone. A failed
            // request is not proof of that: an unsupported address, a rejected write or a CNC that
            // was momentarily busy all surface as exceptions here. Evicting on those disconnects
            // and disposes a driver that background collection tasks are actively using, which is
            // how one bad manual read used to stop collection for that device entirely.
            if (driver.State == ConnectionState.Connected)
            {
                _logger.LogWarning(ex,
                    "Pooled operation failed for device {DeviceId}, keeping the connection (still connected)", deviceId);
                return new(false, default, ex.Message);
            }

            _logger.LogWarning(ex,
                "Pooled operation failed for device {DeviceId} and the driver is {State}, evicting connection",
                deviceId, driver.State);
            await _pool.ReleaseAsync(deviceId, CancellationToken.None);
            return new(false, default, ex.Message);
        }
    }
}
