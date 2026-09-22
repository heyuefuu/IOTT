using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.HuazhongRobot;
using IndustrialIoT.Protocols.Modbus;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ModbusRegressionTests
{
    public static async Task ByteOrderAsync()
    {
        foreach (var gateway in new[] { false, true })
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var requests = new List<byte[]>();
            var peer = ServeAsync(listener, 2, request =>
            {
                requests.Add(request);
                return request[0] == 6 ? request : [3, 2, 0xFF, 0x85];
            }, timeout.Token);
            await using IProtocolDriver driver = gateway
                ? new ProfibusDriver(NullLogger<ProfibusDriver>.Instance, NullLogger<ModbusTcpDriver>.Instance)
                : new ModbusTcpDriver(NullLogger<ModbusTcpDriver>.Instance);
            var connected = await driver.ConnectAsync(Config(listener));
            TestSupport.Require(connected.Success, connected.ErrorMessage ?? "Modbus connect failed");
            var written = await driver.WriteTagAsync("HR0", DataType.Int16, (short)-123, timeout.Token);
            var read = await driver.ReadTagAsync("HR0", DataType.Int16, timeout.Token);
            await peer;
            TestSupport.Require(written.Success, written.ErrorMessage ?? "Register write failed");
            TestSupport.Require(requests[0].SequenceEqual(new byte[] { 6, 0, 0, 0xFF, 0x85 }), "FC06 register byte order is wrong");
            TestSupport.Require(read.Quality == TagQuality.Good && Equals(read.Value, (short)-123), "FC03 register byte order is wrong");
        }
    }

    public static async Task RobotHealthAddressAsync()
    {
        foreach (var explicitAddress in new[] { false, true })
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var expectedAddress = explicitAddress ? 250 : 240;
            var peer = ServeAsync(listener, 1, request =>
                request[0] == 3 && BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1)) == expectedAddress
                    ? [3, 2, 0, 1] : [(byte)(request[0] | 0x80), 2], timeout.Token);
            var addresses = new HuazhongRobotAddressSpace([
                new("/Robot/State/Ready", "Ready", "240", DataType.UInt16, false),
            ]);
            await using var driver = new HuazhongRobotDriver(NullLogger<HuazhongRobotDriver>.Instance, addresses);
            var config = Config(listener) with
            {
                ExtendedProperties = explicitAddress ? new() { ["PingAddress"] = "250" } : new(),
            };
            TestSupport.Require((await driver.ConnectAsync(config)).Success, "Robot connect failed");
            var ready = await driver.PingAsync(timeout.Token);
            await peer;
            TestSupport.Require(ready, "Robot ignored configured health address or mapping");
        }
    }

    private static DeviceConnectionConfig Config(TcpListener listener) => new()
    {
        Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
        ConnectTimeout = TimeSpan.FromSeconds(2), ReadTimeout = TimeSpan.FromSeconds(2),
    };

    private static async Task ServeAsync(TcpListener listener, int count,
        Func<byte[], byte[]> respond, CancellationToken ct)
    {
        using var socket = await listener.AcceptTcpClientAsync(ct);
        using var stream = socket.GetStream();
        for (var index = 0; index < count; index++)
        {
            var header = new byte[7];
            await stream.ReadExactlyAsync(header, ct);
            var pdu = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) - 1];
            await stream.ReadExactlyAsync(pdu, ct);
            var response = respond(pdu);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)(response.Length + 1));
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(response, ct);
        }
    }
}
