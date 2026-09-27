using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Host.Controllers;
using IndustrialIoT.Infrastructure.BackgroundServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.HuazhongRobot;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class HuazhongRobotRegressionTests
{
    public static async Task RunAsync()
    {
        await MappingAndValidationAsync();
        await ImportToBrowseTreeAsync();
        await ImportValidationAsync();
        await ImportConsistencyAsync();
        await WireTypesAsync();
        await ConnectionHealthAsync();
        await StringWritesAsync();
        await LifecycleAsync();
    }

    private static async Task MappingAndValidationAsync()
    {
        using var listener = Listener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requests = new List<byte[]>();
        var peer = ServeAsync(listener, 1, (_, request) =>
        {
            requests.Add(request);
            return Task.FromResult<byte[]>([3, 2, 0, 42]);
        }, timeout.Token);
        var global = new HuazhongRobotAddressSpace([
            new("/Robot/State/Ready", "Global", "100", DataType.UInt16, false)]);
        await using var driver = Driver(global);
        await using var other = Driver(global);
        var map = JsonSerializer.Serialize(new[] { new
        {
            Path = "/Robot/State/Ready", DisplayName = "Local", ModbusAddress = "240",
            DataType = "UInt16", IsWritable = true,
        }});
        TestSupport.Require((await driver.ConnectAsync(Config(listener, new() { ["AddressMap"] = map }))).Success,
            "Device address map was rejected");
        var wrongRead = await driver.ReadTagAsync("/Robot/State/Ready", DataType.Float, timeout.Token);
        var wrongWrite = await driver.WriteTagAsync("/Robot/State/Ready", DataType.Float, 1.5f, timeout.Token);
        var unknown = await driver.ReadTagAsync("/Robot/Missing", DataType.UInt16, timeout.Token);
        var byteRead = await driver.ReadTagAsync("240", DataType.UInt8, timeout.Token);
        TestSupport.Require(wrongRead.Quality == TagQuality.Bad && !wrongWrite.Success
            && unknown.Quality == TagQuality.Bad && byteRead.Quality == TagQuality.Bad,
            "Invalid aliases or types were accepted");
        var local = await driver.BrowseAsync("/Robot/State", timeout.Token);
        var shared = await other.BrowseAsync("/Robot/State", timeout.Token);
        TestSupport.Require(local.Count == 1 && local[0].Path == "/Robot/State/Ready"
            && local[0].DisplayName == "Local" && shared.Count == 1 && shared[0].DisplayName == "Global",
            "Parent browsing or per-device map isolation failed");
        var value = await driver.ReadTagAsync("/Robot/State/Ready", DataType.UInt16, timeout.Token);
        await peer;
        TestSupport.Require(value.Quality == TagQuality.Good && Equals(value.Value, (ushort)42)
            && requests.Count == 1 && requests[0].SequenceEqual(new byte[] { 3, 0, 240, 0, 1 }),
            "Invalid requests reached transport or the device mapping was ignored");
    }

    private static async Task ImportToBrowseTreeAsync()
    {
        var device = new Device
        {
            Id = "huazhong-import-1",
            Name = "Huazhong import fixture",
            Type = DeviceType.Robot,
            Brand = "HSR",
            Model = "HSR",
            Protocol = IndustrialIoT.Domain.Enums.ProtocolType.HuazhongRobot,
            ConnectionConfig = new() { Host = "127.0.0.1", Port = 1 },
        };
        var devices = new ImportDeviceRepository(device);
        var profiles = new ImportProfileRepository();
        var pool = new ImportConnectionPool();
        var controller = new BatchImportController(devices, profiles,
            NullLogger<BatchImportController>.Instance, pool, new(), new ImportRepository(devices, profiles));
        const string csv = "Address,DisplayName,ModbusAddress,DataType,IsWritable,GroupName,IntervalMs\n" +
            "/Robot/State/Ready,Ready,240,UInt16,true,State,1000";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));
        var file = new FormFile(stream, 0, stream.Length, "file", "robot-tags.csv");
        var result = (BatchImportResult)((OkObjectResult)(await controller.ImportTags(device.Id, file, default)).Result!).Value!;

        TestSupport.Require(result.TotalRows == 1 && result.SuccessCount == 1 && result.ErrorCount == 0,
            "Huazhong robot tag import failed");
        TestSupport.Require(profiles.AddCalls == 1 && devices.Updated is not null && pool.Released == device.Id,
            "Successful import did not persist the profile and device map");
        var map = devices.Updated!.ConnectionConfig.ExtendedProperties["AddressMap"];
        TestSupport.Require(result.AddressMap == map, "Import response omitted the map required by the gateway");
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var nodes = JsonSerializer.Deserialize<HuazhongRobotAddressSpace.Node[]>(map, options)!;
        TestSupport.Require(nodes.Length == 1 && nodes[0].ModbusAddress == "240" && nodes[0].IsWritable,
            "Successful import did not populate the address map");
        using var listener = Listener();
        await using var driver = Driver();
        TestSupport.Require((await driver.ConnectAsync(Config(listener, device.ConnectionConfig.ExtendedProperties))).Success,
            "Imported device mapping could not be loaded on connection");
        var browse = await driver.BrowseAsync(null, default);
        var children = await driver.BrowseAsync("/Robot/State", default);
        TestSupport.Require(browse.Count == 1 && browse[0].Path == "/Robot/State"
            && children.Count == 1 && children[0].Path == "/Robot/State/Ready"
            && children[0].DisplayName == "Ready" && children[0].IsWritable,
            "Imported robot nodes were not visible in the browse tree");
    }

    private static async Task ImportValidationAsync()
    {
        var global = new HuazhongRobotAddressSpace([
            new("/Robot/Ready", "Ready", "240", DataType.UInt16, true),
            new("/Robot/Speed", "Speed", "250", DataType.Float, false)]);
        var device = new Device { Id = "import-validation", Name = "Robot", Type = DeviceType.Robot,
            Brand = "HSR", Model = "HSR", Protocol = IndustrialIoT.Domain.Enums.ProtocolType.HuazhongRobot,
            ConnectionConfig = new() { Host = "127.0.0.1", Port = 1 } };
        var devices = new ImportDeviceRepository(device);
        var profiles = new ImportProfileRepository();
        var pool = new ImportConnectionPool();
        var controller = new BatchImportController(devices, profiles,
            NullLogger<BatchImportController>.Instance, pool, global, new ImportRepository(devices, profiles));
        foreach (var (address, dataType, expectedSuccess) in new[] {
            ("/Robot/New", "UInt16", false), ("/Robot/Ready", "999", false),
            ("/Robot/Ready", "Float", false), ("/Robot/Ready", "UInt16", true) })
        {
            var payload = JsonSerializer.Serialize(new[] { new { address, dataType, groupName = "Main", intervalMs = 1000 } });
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload));
            var file = new FormFile(stream, 0, stream.Length, "file", "tags.json");
            var result = (BatchImportResult)((OkObjectResult)(await controller.ImportTags(device.Id, file, default)).Result!).Value!;
            TestSupport.Require((result.SuccessCount == 1) == expectedSuccess, "Import validation returned an unexpected result");
            if (!expectedSuccess)
                TestSupport.Require(devices.Updated is null && profiles.AddCalls == 0 && pool.Released is null,
                    "Invalid import mutated configuration or a live connection");
        }
        var options = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var firstMap = device.ConnectionConfig.ExtendedProperties["AddressMap"];
        var nodes = JsonSerializer.Deserialize<HuazhongRobotAddressSpace.Node[]>(firstMap, options)!;
        TestSupport.Require(nodes.Length == 2 && nodes.Single(node => node.Path == "/Robot/Ready") is
            { ModbusAddress: "240", IsWritable: true }, "Import lost inherited mapping or write permissions");
        using var nextStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "Address,ModbusAddress,DataType,GroupName,IntervalMs\n/Robot/New,260,UInt16,Main,1000"));
        var nextFile = new FormFile(nextStream, 0, nextStream.Length, "file", "tags.csv");
        await controller.ImportTags(device.Id, nextFile, default);
        nodes = JsonSerializer.Deserialize<HuazhongRobotAddressSpace.Node[]>(device.ConnectionConfig.ExtendedProperties["AddressMap"], options)!;
        TestSupport.Require(nodes.Length == 3 && global.All.Count == 2 && profiles.AddCalls == 2,
            "Repeated import replaced earlier points or mutated global mappings");
    }

    private static async Task WireTypesAsync()
    {
        using var listener = Listener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requests = new List<byte[]>();
        var peer = ServeAsync(listener, 8, (header, request) =>
        {
            TestSupport.Require(header[6] == 7, "Configured station was ignored");
            requests.Add(request);
            if (request[0] == 16) return Task.FromResult(request[..5]);
            var address = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1));
            byte[] bytes = address switch
            {
                250 or 270 => Enumerable.Repeat((byte)255, 8).ToArray(),
                260 => [0x3f, 0x80, 0, 0],
                280 => [0x3f, 0xf8, 0, 0, 0, 0, 0, 0],
                290 => System.Text.Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOP"),
                300 => [0xab, 0xcd],
                _ => throw new InvalidOperationException("Unexpected wire address " + address),
            };
            TestSupport.Require(BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3)) * 2 == bytes.Length,
                "Unexpected register count for " + address);
            return Task.FromResult(new byte[] { 3, (byte)bytes.Length }.Concat(bytes).ToArray());
        }, timeout.Token);
        await using var driver = Driver();
        TestSupport.Require((await driver.ConnectAsync(Config(listener,
            new() { ["Station"] = "7", ["DataFormat"] = "ABCD" }))).Success, "Robot connect failed");
        TestSupport.Require(await driver.PingAsync(timeout.Token), "Unmapped connection was rejected by ping");
        var write = await driver.WriteTagAsync("250", DataType.UInt64, ulong.MaxValue.ToString(), timeout.Token);
        var unsigned = await driver.ReadTagAsync("250", DataType.UInt64, timeout.Token);
        var single = await driver.ReadTagAsync("260", DataType.Float, timeout.Token);
        var signed = await driver.ReadTagAsync("270", DataType.Int64, timeout.Token);
        var number = await driver.ReadTagAsync("280", DataType.Double, timeout.Token);
        var text = await driver.ReadTagAsync("290", DataType.String, timeout.Token);
        var raw = await driver.ReadTagAsync("300", DataType.ByteArray, timeout.Token);
        var rawPing = await driver.PingAsync(timeout.Token);
        TestSupport.Require(rawPing, "Last successful raw point was not used by ping");
        await peer;
        ValidateWireValues(write.Success, unsigned, single, signed, number, text, raw, requests);
    }

    private static void ValidateWireValues(bool written, TagValue unsigned, TagValue single,
        TagValue signed, TagValue number, TagValue text, TagValue raw, List<byte[]> requests)
    {
        TestSupport.Require(written && requests[0].SequenceEqual(new byte[]
            { 16, 0, 250, 0, 4, 8, 255, 255, 255, 255, 255, 255, 255, 255 }),
            "UInt64 string write lost integer precision");
        TestSupport.Require(unsigned.Quality == TagQuality.Good && Equals(unsigned.Value, "18446744073709551615"),
            "UInt64 read lost precision");
        TestSupport.Require(single.Quality == TagQuality.Good && Equals(single.Value, 1f), "Float byte order ignored");
        TestSupport.Require(signed.Quality == TagQuality.Good && Equals(signed.Value, -1L), "Int64 read failed");
        TestSupport.Require(number.Quality == TagQuality.Good && Equals(number.Value, 1.5d), "Double read failed");
        TestSupport.Require(text.Quality == TagQuality.Good && Equals(text.Value, "ABCDEFGHIJKLMNOP"),
            "String read or default length is incorrect");
        TestSupport.Require(raw.Quality == TagQuality.Good && raw.Value is byte[] bytes
            && bytes.SequenceEqual(new byte[] { 0xab, 0xcd }), "ByteArray read or default length is incorrect");
        TestSupport.Require(requests[^1].SequenceEqual(requests[^2]), "Ping did not reuse the last successful point");
    }

    private static async Task LifecycleAsync()
    {
        foreach (var dispose in new[] { false, true })
        {
            using var listener = Listener();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var peer = ServeAsync(listener, 1, async (_, request) =>
            {
                received.TrySetResult();
                await release.Task.WaitAsync(timeout.Token);
                return [3, 2, 0, 7];
            }, timeout.Token);
            await using var driver = Driver();
            TestSupport.Require((await driver.ConnectAsync(Config(listener))).Success, "Robot connect failed");
            var read = driver.ReadTagAsync("240", DataType.UInt16, timeout.Token);
            await received.Task.WaitAsync(timeout.Token);
            try
            {
                using var canceled = new CancellationTokenSource();
                var waiting = driver.ReadTagAsync("241", DataType.UInt16, canceled.Token);
                canceled.Cancel();
                try
                {
                    var result = await waiting;
                    TestSupport.Require(result.Quality == TagQuality.Bad, "Canceled waiting read was accepted");
                }
                catch (OperationCanceledException) { }
                var closing = dispose ? driver.DisposeAsync().AsTask() : driver.DisconnectAsync(timeout.Token);
                await Task.Delay(40, timeout.Token);
                TestSupport.Require(!closing.IsCompleted, "Connection closed while a wire read was still in flight");
                release.TrySetResult();
                TestSupport.Require((await read).Quality == TagQuality.Good, "Closing interrupted the in-flight read");
                await closing;
                var afterRead = await driver.ReadTagAsync("240", DataType.UInt16, timeout.Token);
                var afterWrite = await driver.WriteTagAsync("240", DataType.UInt16, (ushort)8, timeout.Token);
                TestSupport.Require(afterRead.Quality == TagQuality.Bad && !afterWrite.Success,
                    "Disposed/disconnected driver accepted a subsequent request");
                await peer;
            }
            finally { release.TrySetResult(); }
        }
    }

    private static HuazhongRobotDriver Driver(HuazhongRobotAddressSpace? space = null) =>
        new(NullLogger<HuazhongRobotDriver>.Instance, space ?? new());

    private static DeviceConnectionConfig Config(TcpListener listener,
        Dictionary<string, string>? properties = null) => new()
    {
        Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
        ConnectTimeout = TimeSpan.FromSeconds(2), ReadTimeout = TimeSpan.FromSeconds(3),
        ExtendedProperties = properties ?? new(),
    };

    private static TcpListener Listener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static async Task ServeAsync(TcpListener listener, int count,
        Func<byte[], byte[], Task<byte[]>> respond, CancellationToken ct)
    {
        using var socket = await listener.AcceptTcpClientAsync(ct);
        using var stream = socket.GetStream();
        for (var index = 0; index < count; index++)
        {
            var header = new byte[7];
            await stream.ReadExactlyAsync(header, ct);
            var request = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) - 1];
            await stream.ReadExactlyAsync(request, ct);
            var response = await respond(header, request);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)(response.Length + 1));
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(response, ct);
        }
    }

    private sealed class ImportConnectionPool : IDeviceConnectionPool
    {
        public string? Released { get; private set; }
        public int ActiveConnections => 0;
        public Task<IProtocolDriver> GetOrCreateAsync(string deviceId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ReleaseAsync(string deviceId, CancellationToken ct = default)
        {
            Released = deviceId;
            return Task.CompletedTask;
        }
        public bool TryGetConnected(string deviceId, out IProtocolDriver? driver)
        {
            driver = null;
            return false;
        }
    }

    private sealed class ImportDeviceRepository(Device device) : IDeviceRepository
    {
        private Device? _stored = device;
        public Device? Updated { get; private set; }
        public int Reads { get; private set; }
        public Task<Device?> GetByIdAsync(string deviceId, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(_stored?.Id == deviceId ? _stored : null);
        }
        public Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Device>> GetByTypeAsync(DeviceType type, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddAsync(Device added, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Device updated, CancellationToken ct = default)
        {
            _stored = updated;
            Updated = updated;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string deviceId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ImportProfileRepository : ICollectionProfileRepository
    {
        public int AddCalls { get; private set; }
        public Task<CollectionProfile?> GetByIdAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CollectionProfile>> GetByDeviceIdAsync(string deviceId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddAsync(CollectionProfile profile, CancellationToken ct = default)
        {
            AddCalls++;
            return Task.CompletedTask;
        }
        public Task UpdateAsync(CollectionProfile profile, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
