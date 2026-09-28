using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Haas;
using Microsoft.Extensions.Logging.Abstractions;

internal static class HaasRegressionTests
{
    public static async Task MacroRoundtripAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var exchanges = new[]
            {
                ("Q100", ">Q100 CNC123"),
                ("E10001 12.5", ">E10001 12.5"),
                ("Q600 10001", "MACRO, 10001, 12.5"),
                ("E10002 1", ">E10002 1"),
                ("Q600 10002", "MACRO, 10002, 1"),
                ("E10003 553", ">E10003 553"),
                ("Q600 10003", "MACRO, 10003, 553"),
            };
            foreach (var (command, response) in exchanges)
            {
                TestSupport.Require(await reader.ReadLineAsync(timeout.Token) == command, "Unexpected MDC command");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "\r\n"), timeout.Token);
            }
        }, timeout.Token);
        await using var driver = new HaasMdcDriver(NullLogger<HaasMdcDriver>.Instance);
        TestSupport.Require((await driver.ConnectAsync(Config(listener, 1000))).Success, "MDC connect failed");
        TestSupport.Require(await driver.PingAsync(timeout.Token), "MDC ping failed");
        TestSupport.Require((await driver.WriteTagAsync("Macro:10001", DataType.Double, 12.5, timeout.Token)).Success, "Macro write failed");
        var read = await driver.ReadTagAsync("Macro:10001", DataType.Double, timeout.Token);
        TestSupport.Require(read.Quality == TagQuality.Good && Equals(read.Value, 12.5), "Macro readback failed");
        TestSupport.Require((await driver.WriteTagAsync("Macro:10002", DataType.Bool, true, timeout.Token)).Success, "Bool macro write failed");
        var booleanRead = await driver.ReadTagAsync("Macro:10002", DataType.Bool, timeout.Token);
        TestSupport.Require(booleanRead.Quality == TagQuality.Good && Equals(booleanRead.Value, true), "Bool macro readback failed");
        TestSupport.Require((await driver.WriteTagAsync("Macro:10003", DataType.Int32, 553, timeout.Token)).Success, "Integer macro write failed");
        var integerRead = await driver.ReadTagAsync("Macro:10003", DataType.Int32, timeout.Token);
        TestSupport.Require(integerRead.Quality == TagQuality.Good && Equals(integerRead.Value, 553), "Integer macro readback failed");
        await peer;
    }

    public static async Task FragmentedResponseAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            TestSupport.Require(await reader.ReadLineAsync(cancellation.Token) == "Q104", "Wrong command");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(">Q104 I"), cancellation.Token);
            await Task.Delay(40, cancellation.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("DLE\r"), cancellation.Token);
            await Task.Delay(40, cancellation.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("\n"), cancellation.Token);
            TestSupport.Require(await reader.ReadLineAsync(cancellation.Token) == "Q402", "Wrong second command");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(">Q402 42\r\n"), cancellation.Token);
        }, cancellation.Token);
        await using var driver = new HaasMdcDriver(NullLogger<HaasMdcDriver>.Instance);
        var connected = await driver.ConnectAsync(Config(listener, 1000));
        TestSupport.Require(connected.Success, connected.ErrorMessage ?? "Connect failed");
        var first = await driver.ReadTagAsync("Mode", DataType.String, cancellation.Token);
        var second = await driver.ReadTagAsync("PartCount", DataType.Int32, cancellation.Token);
        await peer;
        TestSupport.Require(first.Quality == TagQuality.Good && Equals(first.Value, "IDLE"), "Fragment truncated: " + first.Value);
        TestSupport.Require(second.Quality == TagQuality.Good && Equals(second.Value, 42), "Previous frame leaked: " + second.Value);
    }

    public static async Task LabelledResponsesAsync()
    {
        var cases = new (string Address, string Command, DataType Type, string Response, object Value, TagQuality Quality)[]
        {
            ("Mode", "Q104", DataType.String, ">MODE, (MEM)", "(MEM)", TagQuality.Good),
            ("PartCount", "Q402", DataType.Int32, ">M30 #1, 553", 553, TagQuality.Good),
            ("PartCount2", "Q403", DataType.Int32, ">Q403 M30 #2, 554", 554, TagQuality.Good),
            ("Status", "Q500", DataType.String, ">Q500 IDLE, NO PROGRAM, 0", "IDLE, NO PROGRAM, 0", TagQuality.Good),
            ("Status", "Q500", DataType.String, ">STATUS, PROGRAM O12345, PARTS 123", "STATUS, PROGRAM O12345, PARTS 123", TagQuality.Good),
            ("Macro:10001", "Q600 10001", DataType.Double, ">MACRO, 10001, -12.5", -12.5, TagQuality.Good),
            ("PartCount", "Q402", DataType.Int32, ">M30 #1, invalid-number", "", TagQuality.Bad),
            ("Mode", "Q104", DataType.String, ">ERROR, DATA UNAVAILABLE", "", TagQuality.Bad),
            ("Mode", "Q104", DataType.String, ">MODE, ERROR", "", TagQuality.Bad),
        };
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            foreach (var sample in cases)
            {
                TestSupport.Require(await reader.ReadLineAsync(timeout.Token) == sample.Command, "Wrong labelled query");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(sample.Response + "\r\n"), timeout.Token);
            }
        }, timeout.Token);
        await using var driver = new HaasMdcDriver(NullLogger<HaasMdcDriver>.Instance);
        TestSupport.Require((await driver.ConnectAsync(Config(listener, 1000))).Success, "MDC connect failed");
        foreach (var sample in cases)
        {
            var read = await driver.ReadTagAsync(sample.Address, sample.Type, timeout.Token);
            TestSupport.Require(read.Quality == sample.Quality && Equals(read.Value, sample.Value),
                $"Unexpected labelled response {sample.Response}: {read.Quality} {read.Value}");
        }
        await peer;
    }

    public static Task ReadTimeoutAsync() => TimeoutAsync(false);
    public static Task PartialResponseTimeoutAsync() => TimeoutAsync(true);

    private static async Task TimeoutAsync(bool partial)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await reader.ReadLineAsync(cancellation.Token);
            if (partial) await stream.WriteAsync(Encoding.ASCII.GetBytes(">Q104 INCOMPLETE"), cancellation.Token);
            try { await Task.Delay(Timeout.Infinite, cancellation.Token); }
            catch (OperationCanceledException) { }
        }, cancellation.Token);
        await using var driver = new HaasMdcDriver(NullLogger<HaasMdcDriver>.Instance);
        TestSupport.Require((await driver.ConnectAsync(Config(listener, 120))).Success, "Connect failed");
        var timer = Stopwatch.StartNew();
        var read = await driver.ReadTagAsync("Mode", DataType.String, cancellation.Token);
        cancellation.Cancel();
        await peer;
        TestSupport.Require(read.Quality == TagQuality.Bad, "Incomplete response falsely succeeded");
        TestSupport.Require(timer.Elapsed < TimeSpan.FromSeconds(1.5), "ReadTimeout did not bound async read");
        TestSupport.Require(driver.State == ConnectionState.Faulted, "Timed-out connection kept stale response channel");
    }

    private static DeviceConnectionConfig Config(TcpListener listener, int timeoutMs) => new()
    {
        Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
        ReadTimeout = TimeSpan.FromMilliseconds(timeoutMs), ConnectTimeout = TimeSpan.FromSeconds(2),
    };
}
