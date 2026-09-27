using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using IndustrialIoT.Domain.Enums;

internal static partial class HuazhongRobotRegressionTests
{
    private static async Task ConnectionHealthAsync()
    {
        foreach (var readFirst in new[] { false, true })
        {
            using var listener = Listener();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var accepted = listener.AcceptTcpClientAsync(timeout.Token);
            await using var driver = Driver();
            TestSupport.Require((await driver.ConnectAsync(Config(listener))).Success, "Health fixture connect failed");
            using var socket = await accepted;
            TestSupport.Require(await driver.PingAsync(timeout.Token), "Healthy unmapped socket was rejected");
            socket.Client.LingerState = new LingerOption(true, 0);
            socket.Close();
            listener.Stop();
            if (readFirst)
                TestSupport.Require((await driver.ReadTagAsync("240", DataType.UInt16, timeout.Token)).Quality == TagQuality.Bad,
                    "Read succeeded after the peer closed");
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (await driver.PingAsync(timeout.Token) && DateTime.UtcNow < deadline)
                await Task.Delay(10, timeout.Token);
            TestSupport.Require(!await driver.PingAsync(timeout.Token) && driver.State == ConnectionState.Faulted,
                "Closed unmapped socket was still reported as connected");
        }

        using var liveListener = Listener();
        using var liveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var peer = ServeAsync(liveListener, 1, (_, _) => Task.FromResult<byte[]>([0x83, 2]), liveTimeout.Token);
        await using var liveDriver = Driver();
        await liveDriver.ConnectAsync(Config(liveListener));
        TestSupport.Require((await liveDriver.ReadTagAsync("240", DataType.UInt16)).Quality == TagQuality.Bad
            && !await liveDriver.PingAsync(), "Failed first read was followed by a successful empty heartbeat");
        await peer;
    }

    private static async Task StringWritesAsync()
    {
        foreach (var (length, value, expected) in new[] {
            (1, "a", new byte[] { 97, 0 }),
            (3, "abc", new byte[] { 97, 98, 99, 0 }),
            (4, "中文", new byte[] { 0xe4, 0xb8, 0xad, 0 }),
            (5, "a😀z", new byte[] { 97, 0xf0, 0x9f, 0x98, 0x80, 0 }),
            (246, new string('x', 246), Enumerable.Repeat((byte)120, 246).ToArray()) })
        {
            using var listener = Listener();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var peer = ServeAsync(listener, length > 240 ? 3 : 2, (_, request) =>
            {
                if (request[0] == 16)
                {
                    var registers = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3));
                    TestSupport.Require(registers <= 123 && registers * 2 == request[5]
                        && request[6..].SequenceEqual(expected), "String write emitted invalid registers or split UTF-8");
                    return Task.FromResult(request[..5]);
                }
                var offset = (BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1)) - 240) * 2;
                var byteCount = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3)) * 2;
                return Task.FromResult(new byte[] { 3, (byte)byteCount }
                    .Concat(expected.Skip(offset).Take(byteCount)).ToArray());
            }, timeout.Token);
            await using var driver = Driver();
            await driver.ConnectAsync(Config(listener, new() { ["StringLength"] = length.ToString() }));
            TestSupport.Require((await driver.WriteTagAsync("240", DataType.String, value, timeout.Token)).Success,
                "Valid string write was rejected");
            var read = await driver.ReadTagAsync("240", DataType.String, timeout.Token);
            TestSupport.Require(read.Quality == TagQuality.Good
                && Equals(read.Value, Encoding.UTF8.GetString(expected[..length])),
                $"String read mismatch for {length} bytes: {System.Text.Json.JsonSerializer.Serialize(read.Value)}, {read.ErrorMessage}");
            await peer;
        }
        using var oversizedListener = Listener();
        await using var oversized = Driver();
        await oversized.ConnectAsync(Config(oversizedListener, new() { ["StringLength"] = "247" }));
        var rejected = await oversized.WriteTagAsync("240", DataType.String, "large");
        TestSupport.Require(!rejected.Success && rejected.ErrorMessage!.Contains("246"),
            "String write exceeding the Modbus register limit was accepted");
    }
}
