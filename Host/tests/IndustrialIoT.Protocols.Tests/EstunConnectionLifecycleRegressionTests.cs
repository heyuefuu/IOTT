using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using IndustrialIoT.Application.DTOs;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Host.Controllers;
using IndustrialIoT.Infrastructure.BackgroundServices;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.EstunRobot;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Modbus;
using IndustrialIoT.Protocols.Registration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

internal static class EstunConnectionLifecycleRegressionTests
{
    public static async Task RunAsync()
    {
        await using var peer = new ModbusPeer();
        var device = new Device
        {
            Name = "Estun lifecycle", Type = DeviceType.Robot, Brand = "ESTUN", Model = "ER",
            Protocol = ProtocolType.EstunRobot,
            ConnectionConfig = new DeviceConnectionConfig
            {
                Host = "127.0.0.1", Port = peer.Port,
                ConnectTimeout = TimeSpan.FromSeconds(2), ReadTimeout = TimeSpan.FromSeconds(2),
            },
        };
        var repository = new DeviceRepository(device);
        using var services = new ServiceCollection().AddSingleton<IDeviceRepository>(repository).BuildServiceProvider();
        var factory = new DriverFactory();
        using var pool = new ConnectionPoolService(factory, services, NullLogger<ConnectionPoolService>.Instance);
        var controller = new DevicesController(null!, pool, repository);
        try { await VerifyLifecycleAsync(controller, pool, factory, repository, device, peer); }
        finally { await pool.ReleaseAsync(device.Id); }
        await peer.WaitForCloseAsync();
        TestSupport.Require(peer.Closed == 1, "Explicit release must close the single pooled socket");
        await VerifyIdleKeepAliveAsync(ProtocolType.EstunRobot, null);
        await VerifyIdleKeepAliveAsync(ProtocolType.ModbusTCP, null, brand: "埃斯顿");
        await VerifyIdleKeepAliveAsync(ProtocolType.ModbusTCP, "200", releaseExplicitly: true, expectedIntervalMs: 200);
        await VerifyIdleKeepAliveAsync(ProtocolType.ModbusTCP, "50");
        await VerifyIdleKeepAliveAsync(ProtocolType.ModbusTCP, "5000");
        await VerifyOtherModbusRemainsIdleAsync();
    }

    private static async Task VerifyLifecycleAsync(DevicesController controller, ConnectionPoolService pool,
        DriverFactory factory, DeviceRepository repository, Device device, ModbusPeer peer)
    {
        var first = await controller.TestConnection(device.Id);
        RequireSuccess(first, "Initial connection test failed");
        TestSupport.Require(device.Status == DeviceStatus.Online && device.LastSeenAt is not null,
            "Successful connection test must persist online state and last seen time");
        TestSupport.Require(pool.ActiveConnections == 1 && peer.Accepted == 1 && peer.Closed == 0,
            "Initial connection test must retain exactly one connection");
        RequireSuccess(await controller.TestConnection(device.Id), "Repeated connection test failed");
        TestSupport.Require(peer.Requests >= 2, "Repeated connection test did not probe the existing socket");
        var transfer = new ProgramTransferController(factory, repository, null!, null!, null!,
            NullLogger<ProgramTransferController>.Instance);
        var capabilities = await transfer.GetCapabilities(device.Id);
        TestSupport.Require(capabilities.Result is OkObjectResult { Value: ProgramTransferCapability { SupportsBrowse: false } },
            "Estun must not advertise program file browsing capability");
        var files = await transfer.GetFiles(device.Id);
        TestSupport.Require(files.Result is BadRequestObjectResult,
            "Estun address-space browsing must not be offered as program file browsing");
        var driver = await pool.GetOrCreateAsync(device.Id);
        var value = await driver.ReadTagAsync("0", DataType.UInt16);
        TestSupport.Require(value.Quality == TagQuality.Good && Convert.ToUInt16(value.Value) == 0,
            "Register reads must remain usable after the rejected program file request");
        await Task.Delay(100);
        TestSupport.Require(peer.Accepted == 1 && peer.Closed == 0,
            "Testing or listing unsupported files opened or closed an extra socket");
        peer.RejectReads = true;
        var lastSeen = device.LastSeenAt;
        var failed = await controller.TestConnection(device.Id);
        TestSupport.Require(failed.Result is OkObjectResult { Value: ConnectionTestResult { Success: false } },
            "Modbus exception must produce a failed connection test");
        TestSupport.Require(device.Status == DeviceStatus.Error && device.LastSeenAt == lastSeen,
            "Failed probe must persist error state without advancing last seen time");
        TestSupport.Require(peer.Accepted == 1 && peer.Closed == 0,
            "A failed probe must not close the shared connection");
        peer.RejectReads = false;
        RequireSuccess(await controller.TestConnection(device.Id), "Connection did not recover after a failed probe");
        TestSupport.Require((await controller.TestConnection("missing-device")).Result is NotFoundResult,
            "Unknown device must return not found");
    }

    private static async Task VerifyIdleKeepAliveAsync(ProtocolType protocol, string? interval,
        string brand = "ESTUN", bool releaseExplicitly = false, int expectedIntervalMs = 1000)
    {
        await using var peer = new ModbusPeer(TimeSpan.FromSeconds(2.5));
        var device = new Device
        {
            Name = "Estun idle", Type = DeviceType.Robot, Brand = brand, Model = "ER", Protocol = protocol,
            ConnectionConfig = new DeviceConnectionConfig
            {
                Host = "127.0.0.1", Port = peer.Port,
                ConnectTimeout = TimeSpan.FromSeconds(1), ReadTimeout = TimeSpan.FromSeconds(1),
                ExtendedProperties = interval is null ? new() : new() { ["KeepAliveIntervalMs"] = interval },
            },
        };
        var repository = new DeviceRepository(device);
        using var services = new ServiceCollection().AddSingleton<IDeviceRepository>(repository).BuildServiceProvider();
        using var pool = new ConnectionPoolService(new DriverFactory(), services, NullLogger<ConnectionPoolService>.Instance);
        await pool.StartAsync(CancellationToken.None);
        try
        {
            var controller = new DevicesController(null!, pool, repository);
            using var request = new CancellationTokenSource();
            RequireSuccess(await controller.TestConnection(device.Id, request.Token), protocol + " initial idle connection failed");
            await request.CancelAsync();
            var initialRequests = peer.Requests;
            var useDefaultInterval = expectedIntervalMs == 1000;
            await Task.Delay(TimeSpan.FromSeconds(useDefaultInterval ? 4.2 : 1.2));
            TestSupport.Require(peer.ReceiveTimeouts == 0,
                protocol + " controller receive timeout: no keepalive arrived within 2.5 seconds");
            var keepAliveRequests = peer.Requests - initialRequests;
            TestSupport.Require(keepAliveRequests >= (useDefaultInterval ? 3 : 4),
                protocol + " did not continue sending FC03 requests while the page was idle");
            TestSupport.Require(keepAliveRequests <= (useDefaultInterval ? 6 : 9),
                protocol + " sent keepalive requests too frequently for the expected interval");
            TestSupport.Require(peer.Accepted == 1 && peer.Closed == 0 && pool.ActiveConnections == 1,
                protocol + " idle keepalive must preserve the original pooled socket");
            if (releaseExplicitly)
            {
                await pool.ReleaseAsync(device.Id);
                await peer.WaitForCloseAsync();
                var releasedRequests = peer.Requests;
                await Task.Delay(TimeSpan.FromSeconds(1.2));
                TestSupport.Require(peer.Accepted == 1 && peer.Closed == 1 && peer.Requests == releasedRequests,
                    protocol + " explicit release must stop keepalive while the pool is still running");
                TestSupport.Require(pool.ActiveConnections == 0, "Explicit release must remove the pooled connection");
            }
        }
        finally { await pool.StopAsync(CancellationToken.None); }
        await peer.WaitForCloseAsync();
        var stoppedRequests = peer.Requests;
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        TestSupport.Require(peer.Accepted == 1 && peer.Closed == 1 && peer.Requests == stoppedRequests,
            protocol + " keepalive must not reconnect or send requests after pool shutdown");
    }

    private static async Task VerifyOtherModbusRemainsIdleAsync()
    {
        await using var peer = new ModbusPeer();
        var device = new Device
        {
            Name = "Other Modbus", Type = DeviceType.PLC, Brand = "Inovance", Model = "H3U", Protocol = ProtocolType.ModbusTCP,
            ConnectionConfig = new DeviceConnectionConfig
            {
                Host = "127.0.0.1", Port = peer.Port,
                ConnectTimeout = TimeSpan.FromSeconds(1), ReadTimeout = TimeSpan.FromSeconds(1),
            },
        };
        var repository = new DeviceRepository(device);
        using var services = new ServiceCollection().AddSingleton<IDeviceRepository>(repository).BuildServiceProvider();
        using var pool = new ConnectionPoolService(new DriverFactory(), services, NullLogger<ConnectionPoolService>.Instance);
        await pool.StartAsync(CancellationToken.None);
        try
        {
            var controller = new DevicesController(null!, pool, repository);
            RequireSuccess(await controller.TestConnection(device.Id), "Other Modbus initial connection failed");
            var initialRequests = peer.Requests;
            await Task.Delay(TimeSpan.FromSeconds(2.2));
            TestSupport.Require(peer.Requests == initialRequests && peer.Accepted == 1 && peer.Closed == 0,
                "Non-Estun Modbus devices must retain their normal health-check cadence");
        }
        finally { await pool.StopAsync(CancellationToken.None); }
    }

    private static void RequireSuccess(ActionResult<ConnectionTestResult> result, string message) =>
        TestSupport.Require(result.Result is OkObjectResult { Value: ConnectionTestResult { Success: true } }, message);

    private sealed class DriverFactory : IProtocolDriverFactory
    {
        public IProtocolDriver Create(ProtocolType protocol, string brand, string? model = null) =>
            protocol == ProtocolType.EstunRobot
                ? new EstunRobotDriver(NullLogger<EstunRobotDriver>.Instance)
                : new ModbusTcpDriver(NullLogger<ModbusTcpDriver>.Instance);
    }

    private sealed class DeviceRepository(Device device) : IDeviceRepository
    {
        public Task<Device?> GetByIdAsync(string id, CancellationToken ct = default) => Task.FromResult(id == device.Id ? device : null);
        public Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Device>>([device]);
        public Task<IReadOnlyList<Device>> GetByTypeAsync(DeviceType type, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Device>>(type == device.Type ? [device] : []);
        public Task AddAsync(Device value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Device value, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ModbusPeer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
        private readonly List<Task> _clients = [];
        private readonly Task _acceptLoop;
        private int _accepted;
        private int _closed;
        private int _requests;
        private int _receiveTimeouts;
        private readonly TimeSpan? _idleTimeout;
        public volatile bool RejectReads;
        public int Accepted => Volatile.Read(ref _accepted);
        public int Closed => Volatile.Read(ref _closed);
        public int Requests => Volatile.Read(ref _requests);
        public int ReceiveTimeouts => Volatile.Read(ref _receiveTimeouts);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public ModbusPeer(TimeSpan? idleTimeout = null)
        {
            _idleTimeout = idleTimeout;
            _listener.Start();
            _acceptLoop = AcceptAsync();
        }

        public async Task WaitForCloseAsync()
        {
            while (Closed == 0) await Task.Delay(10, _stop.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _acceptLoop;
            _listener.Stop();
            await Task.WhenAll(_clients);
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _accepted);
                    _clients.Add(ServeAsync(client));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var header = new byte[7];
                        using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        if (_idleTimeout is { } idleTimeout) receiveTimeout.CancelAfter(idleTimeout);
                        try { await stream.ReadExactlyAsync(header, receiveTimeout.Token); }
                        catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                        {
                            Interlocked.Increment(ref _receiveTimeouts);
                            Interlocked.Increment(ref _closed);
                            return;
                        }
                        var request = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) - 1];
                        await stream.ReadExactlyAsync(request, _stop.Token);
                        TestSupport.Require(request[0] == 3, "Expected a read-only FC03 request");
                        var byteCount = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3)) * 2;
                        var response = RejectReads ? new byte[] { 0x83, 2 } : new byte[byteCount + 2];
                        if (!RejectReads) { response[0] = 3; response[1] = checked((byte)byteCount); }
                        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)(response.Length + 1));
                        Interlocked.Increment(ref _requests);
                        await stream.WriteAsync(header, _stop.Token);
                        await stream.WriteAsync(response, _stop.Token);
                    }
                }
                catch (EndOfStreamException) { Interlocked.Increment(ref _closed); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            }
        }
    }
}
