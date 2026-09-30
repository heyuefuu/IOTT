using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Host.Services;
using IndustrialIoT.Infrastructure.BackgroundServices;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Modbus;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ModbusBatchConnectionRegressionTests
{
    public static async Task RunAsync()
    {
        foreach (var mode in new[] { "healthy", "rejected", "closed", "timeout", "invalid-frame" })
            await VerifyAsync(mode);
    }

    private static async Task VerifyAsync(string mode)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var device = new Device
        {
            Name = "Modbus batch", Type = DeviceType.Robot, Brand = "Modbus", Model = "Fixture",
            Protocol = IndustrialIoT.Domain.Enums.ProtocolType.ModbusTCP,
            ConnectionConfig = new DeviceConnectionConfig
            {
                Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                ConnectTimeout = TimeSpan.FromSeconds(1), ReadTimeout = TimeSpan.FromMilliseconds(200),
            },
        };
        using var services = new ServiceCollection().AddSingleton<IDeviceRepository>(new Repository(device)).BuildServiceProvider();
        using var pool = new ConnectionPoolService(new Factory(), services, NullLogger<ConnectionPoolService>.Instance);
        var accessor = new PooledDriverAccessor(pool, NullLogger<PooledDriverAccessor>.Instance);
        var peer = ServeAsync(listener, mode, timeout.Token);
        var broken = mode is "closed" or "timeout" or "invalid-frame";
        try
        {
            var initial = await ReadAsync("40001");
            TestSupport.Require(initial.Success && initial.Value![0].Quality == TagQuality.Good, mode + ": initial single read failed");
            var original = await pool.GetOrCreateAsync(device.Id, timeout.Token);
            var batch = await ReadAsync("40001", "40002", "40003", "40004");
            TestSupport.Require(batch.Success && batch.Value!.Count == 4, mode + ": batch results were lost");
            TestSupport.Require(batch.Value![0].Quality == TagQuality.Good, mode + ": first batch item failed");
            var expectedBad = broken ? 3 : mode == "rejected" ? 1 : 0;
            TestSupport.Require(batch.Value.Count(tag => tag.Quality == TagQuality.Bad) == expectedBad,
                mode + ": incorrect per-point quality");
            TestSupport.Require(batch.Value.Where(tag => tag.Quality == TagQuality.Bad).All(tag => !string.IsNullOrWhiteSpace(tag.ErrorMessage)),
                mode + ": failed points need an error message");
            TestSupport.Require(original.State == (broken ? ConnectionState.Faulted : ConnectionState.Connected),
                mode + ": transport failure was not distinguished from a point rejection; state=" + original.State);
            var final = await ReadAsync("40001");
            TestSupport.Require(final.Success && final.Value![0].Quality == TagQuality.Good && Equals(final.Value[0].Value, 1.5f),
                mode + ": single read after batch did not recover");
            var current = await pool.GetOrCreateAsync(device.Id, timeout.Token);
            TestSupport.Require(ReferenceEquals(original, current) != broken, mode + ": incorrect pooled connection reuse");
            await peer;
        }
        finally
        {
            await timeout.CancelAsync();
            await pool.ReleaseAsync(device.Id);
            try { await peer; } catch (OperationCanceledException) { }
        }

        Task<PooledDriverResult<IReadOnlyList<TagValue>>> ReadAsync(params string[] addresses) =>
            accessor.ExecuteAsync(device.Id, driver => driver.ReadTagsAsync(addresses.Select(address =>
                new TagReadRequest { Address = address, DataType = DataType.Float }).ToArray(), timeout.Token), timeout.Token);
    }

    private static async Task ServeAsync(TcpListener listener, string mode, CancellationToken ct)
    {
        using (var socket = await listener.AcceptTcpClientAsync(ct))
        {
            var stream = socket.GetStream();
            for (var index = 0; index < 6; index++)
            {
                var header = new byte[7];
                await stream.ReadExactlyAsync(header, ct);
                var request = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) - 1];
                await stream.ReadExactlyAsync(request, ct);
                TestSupport.Require(request[0] == 3 && BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3)) == 2,
                    "Float reads must request two holding registers");
                if (index == 2 && mode == "closed") break;
                if (index == 2 && mode == "timeout")
                {
                    var pending = new byte[1];
                    TestSupport.Require(await stream.ReadAsync(pending, ct) == 0, "Timed-out batch reused the damaged stream");
                    break;
                }
                byte[] response = index == 2 && mode == "rejected" ? [0x83, 2] : [3, 4, 0x3F, 0xC0, 0, 0];
                if (index == 2 && mode == "invalid-frame") header[3] = 1;
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)(response.Length + 1));
                await stream.WriteAsync(header, ct);
                await stream.WriteAsync(response, ct);
                if (index == 2 && mode == "invalid-frame") break;
            }
        }
        if (mode is "closed" or "timeout" or "invalid-frame")
        {
            using var socket = await listener.AcceptTcpClientAsync(ct);
            var stream = socket.GetStream();
            var request = new byte[12];
            await stream.ReadExactlyAsync(request, ct);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), 7);
            await stream.WriteAsync(request.AsMemory(0, 7), ct);
            await stream.WriteAsync(new byte[] { 3, 4, 0x3F, 0xC0, 0, 0 }, ct);
        }
    }

    private sealed class Factory : IProtocolDriverFactory
    {
        public IProtocolDriver Create(IndustrialIoT.Domain.Enums.ProtocolType protocol, string brand, string? model = null) =>
            new ModbusTcpDriver(NullLogger<ModbusTcpDriver>.Instance);
    }

    private sealed class Repository(Device device) : IDeviceRepository
    {
        public Task<Device?> GetByIdAsync(string id, CancellationToken ct = default) => Task.FromResult<Device?>(device);
        public Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Device>>([device]);
        public Task<IReadOnlyList<Device>> GetByTypeAsync(DeviceType type, CancellationToken ct = default) => GetAllAsync(ct);
        public Task AddAsync(Device value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Device value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
