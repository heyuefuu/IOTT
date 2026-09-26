using System.Net;
using System.Net.Sockets;
using System.Reflection;
using HslCommunication;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.FINS;
using IndustrialIoT.Protocols.Mewtocol;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.SiemensS7;
using Microsoft.Extensions.Logging.Abstractions;

internal static class PlcSafetyRegressionTests
{
    private static readonly Type ClientType = typeof(S7Address).Assembly.GetType("IndustrialIoT.Protocols.SiemensS7.S7TcpClient")!;

    public static async Task RunAsync()
    {
        await HslTypesAsync();
        AddressBoundsAndRackSlot();
        await SilentHandshakeAsync();
        await WriteAndReadTimeoutAsync(cancelRead: false);
        await WriteAndReadTimeoutAsync(cancelRead: true);
    }

    private static async Task HslTypesAsync()
    {
        IProtocolDriver[] drivers = [new FinsDriver(NullLogger<FinsDriver>.Instance),
            new OmronHostLinkDriver(NullLogger<OmronHostLinkDriver>.Instance),
            new MewtocolDriver(NullLogger<MewtocolDriver>.Instance),
            new MewtocolSerialDriver(NullLogger<MewtocolSerialDriver>.Instance)];
        foreach (var driver in drivers)
        {
            await using var disposable = driver;
            foreach (var dataType in new[] { DataType.Int8, DataType.UInt8, (DataType)999 })
            {
                var read = await driver.ReadTagAsync("D0", dataType);
                var write = await driver.WriteTagAsync("D0", dataType, 1);
                TestSupport.Require(read.Quality == TagQuality.Bad && !string.IsNullOrWhiteSpace(read.ErrorMessage), "Unsupported HSL type must fail before IO");
                TestSupport.Require(!write.Success && !string.IsNullOrWhiteSpace(write.ErrorMessage), "Unsupported HSL type must not write an adjacent byte");
            }
            var convert = driver.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(method => method.Name == "ToTagValue" && method.GetParameters().Length == 3).MakeGenericMethod(typeof(ulong));
            var result = OperateResult.CreateSuccessResult(ulong.MaxValue);
            var tag = (TagValue)convert.Invoke(null, ["D0", DataType.UInt64, result])!;
            TestSupport.Require(tag.Quality == TagQuality.Good && tag.Value is string digits && digits == "18446744073709551615", "HSL UInt64 must preserve all digits");
        }
    }

    private static void AddressBoundsAndRackSlot()
    {
        foreach (var address in new[] { "MB-1", "MB2097152", "DB1.DBB2147483647", "C-1", "T16777216" })
        {
            try { S7Address.Parse(address, S7DataLength.Byte); throw new Exception($"Accepted unsafe address {address}"); }
            catch (FormatException) { }
        }
        try { S7Address.Parse("MB2097151", S7DataLength.Word); throw new Exception("Accepted range crossing the final address"); }
        catch (FormatException) { }
        TestSupport.Require(S7Address.Parse("M2097151.7", S7DataLength.Bit).BitAddress == 0xFFFFFF, "Last valid bit rejected");
        var build = ClientType.GetMethod("BuildCotpConnectionRequest", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var cpu in new[] { SiemensS7PlcType.S300, SiemensS7PlcType.S400 })
        {
            var packet = (byte[])build.Invoke(null, [cpu, (byte)1, (byte)5])!;
            TestSupport.Require(packet[^1] == 0x25, "Explicit S7 rack/slot overwritten");
        }
    }

    private static async Task SilentHandshakeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var driver = new SiemensS7Driver(NullLogger<SiemensS7Driver>.Instance);
        var config = Config(listener, 100, 2000);
        var connect = driver.ConnectAsync(config, deadline.Token);
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await connect;
        TestSupport.Require(!result.Success && result.ErrorMessage!.Contains("timed out", StringComparison.OrdinalIgnoreCase), "Silent handshake must time out");
        TestSupport.Require(timer.Elapsed < TimeSpan.FromSeconds(1.5), "Connection timeout did not bound handshake IO");
    }

    private static async Task WriteAndReadTimeoutAsync(bool cancelRead)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = ServeWriteThenStallAsync(listener, deadline.Token);
        await using var driver = new SiemensS7Driver(NullLogger<SiemensS7Driver>.Instance);
        var connected = await driver.ConnectAsync(Config(listener, 2000, cancelRead ? 2000 : 150), deadline.Token);
        TestSupport.Require(connected.Success, "S7 loopback handshake failed");
        var invalidRead = await driver.ReadTagAsync("MB2097152", DataType.UInt8, deadline.Token);
        var invalidWrite = await driver.WriteTagAsync("MB-1", DataType.UInt8, (byte)1, deadline.Token);
        var invalidValue = await driver.WriteTagAsync("MB0", DataType.UInt8, "invalid", deadline.Token);
        TestSupport.Require(invalidRead.Quality == TagQuality.Bad && !invalidWrite.Success && !invalidValue.Success
            && driver.State == ConnectionState.Connected, "Local address/type validation must preserve an established connection");
        var write = await driver.WriteTagAsync("DB1.DBB0", DataType.ByteArray, new byte[] { 1, 2, 3, 4 }, deadline.Token);
        TestSupport.Require(write.Success, "S7 ByteArray write failed");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        if (cancelRead)
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            request.CancelAfter(100);
            try { await driver.ReadTagAsync("MB0", DataType.UInt8, request.Token); throw new Exception("Read ignored caller cancellation"); }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        }
        else
        {
            var read = await driver.ReadTagAsync("MB0", DataType.UInt8, deadline.Token);
            TestSupport.Require(read.Quality == TagQuality.Bad && read.ErrorMessage!.Contains("timed out", StringComparison.OrdinalIgnoreCase), "Silent S7 read must time out");
        }
        TestSupport.Require(timer.Elapsed < TimeSpan.FromSeconds(1.5), "S7 ReadTimeout was ignored");
        TestSupport.Require(driver.State == ConnectionState.Faulted && !await driver.PingAsync(deadline.Token),
            "A timed-out or cancelled transport must stop advertising Connected");
        await server;
    }

    private static async Task ServeWriteThenStallAsync(TcpListener listener, CancellationToken ct)
    {
        using var socket = await listener.AcceptTcpClientAsync(ct);
        using var stream = socket.GetStream();
        await ReadPacketAsync(stream, ct);
        await stream.WriteAsync(new byte[] { 3, 0, 0, 22, 17, 0xD0, 0, 1, 0, 1, 0, 0xC0, 1, 10, 0xC1, 2, 1, 2, 0xC2, 2, 1, 0 }, ct);
        await ReadPacketAsync(stream, ct);
        await stream.WriteAsync(new byte[] { 3, 0, 0, 27, 2, 0xF0, 0x80, 0x32, 3, 0, 0, 0, 1, 0, 8, 0, 0, 0, 0, 0xF0, 0, 0, 1, 0, 1, 1, 0xE0 }, ct);
        var packet = await ReadPacketAsync(stream, ct);
        TestSupport.Require(packet[23] == 0 && packet[24] == 4 && packet[33] == 0 && packet[34] == 32
            && packet.AsSpan(35).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "S7 ByteArray count differs from payload");
        var ack = new byte[] { 3, 0, 0, 22, 2, 0xF0, 0x80, 0x32, 3, 0, 0, 0, 1, 0, 2, 0, 1, 0, 0, 5, 1, 0xFF };
        packet.AsSpan(11, 2).CopyTo(ack.AsSpan(11));
        await stream.WriteAsync(ack, ct);
        await ReadPacketAsync(stream, ct);
        var closed = await stream.ReadAsync(new byte[1], ct);
        TestSupport.Require(closed == 0, "Timed-out S7 connection must be discarded to avoid stale replies");
    }

    private static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var packet = new byte[(header[2] << 8) | header[3]];
        header.CopyTo(packet, 0);
        await stream.ReadExactlyAsync(packet.AsMemory(4), ct);
        return packet;
    }

    private static DeviceConnectionConfig Config(TcpListener listener, int connectMs, int readMs) => new()
    {
        Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
        ConnectTimeout = TimeSpan.FromMilliseconds(connectMs), ReadTimeout = TimeSpan.FromMilliseconds(readMs),
    };
}
