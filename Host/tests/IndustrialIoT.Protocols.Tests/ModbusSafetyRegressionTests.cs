using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using HslCommunication;
using HslCommunication.ModBus;
using HslCommunication.Profinet.Inovance;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Inovance;
using IndustrialIoT.Protocols.Modbus;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ModbusSafetyRegressionTests
{
    public static async Task RunAsync()
    {
        await VerifyRtuAsync();
        await VerifyTcpWritesAsync(false);
        await VerifyTcpWritesAsync(true);
        await VerifyInovanceAsync();
    }

    private static async Task VerifyRtuAsync()
    {
        using var client = new RecordingRtu();
        await using var driver = new ModbusRtuDriver(NullLogger<ModbusRtuDriver>.Instance);
        SetField(driver, "_client", client);
        SetField(driver, "_state", ConnectionState.Connected);
        foreach (var address in new[] { "C0", "C0;1" })
            TestSupport.Require(!(await driver.WriteTagAsync(address, DataType.UInt16, (ushort)7)).Success,
                "Non-Bool coil write was accepted");
        TestSupport.Require(client.Calls.Count == 0, "Rejected writes reached HSL");
        TestSupport.Require((await driver.WriteTagAsync("C0", DataType.Bool, true)).Success, "Valid coil write failed");
        TestSupport.Require(client.Calls.Last() == "bool:0", "Coil write used a register overload");
        var values = await driver.ReadTagsAsync([
            new() { Address = "invalid", DataType = DataType.UInt16 },
            new() { Address = "HR0", DataType = DataType.UInt16 },
        ]);
        TestSupport.Require(values.Count == 2 && values[0].Quality == TagQuality.Bad
            && values[1].Quality == TagQuality.Good, "One malformed address aborted RTU batch");
        var words = await driver.ReadTagAsync("HR0;3", DataType.UInt16);
        TestSupport.Require(words.Value is ushort[] { Length: 3 }, "RTU explicit word count was lost");
        var floats = await driver.ReadTagAsync("IR0;4", DataType.Float);
        TestSupport.Require(floats.Value is float[] { Length: 2 } && client.Calls.Last() == "float:x=4;0:2",
            "RTU input-register float array used wrong area or element count");
        var bits = await driver.ReadTagAsync("DI0;9", DataType.Bool);
        TestSupport.Require(bits.Value is bool[] { Length: 9 }, "RTU bit count was lost");
        var bytes = await driver.ReadTagAsync("HR0;3", DataType.ByteArray);
        TestSupport.Require(bytes.Value is byte[] { Length: 6 }, "RTU byte array count was lost");
        var text = await driver.ReadTagAsync("HR0;3", DataType.String);
        TestSupport.Require(text.Value is string { Length: 6 }, "RTU string register count was lost");
        var before = client.Calls.Count;
        TestSupport.Require((await driver.ReadTagAsync("HR0;3", DataType.Float)).Quality == TagQuality.Bad,
            "Incomplete float element was accepted");
        TestSupport.Require(client.Calls.Count == before, "Invalid float count reached HSL");
        TestSupport.Require(Equals((await driver.ReadTagAsync("HR0", DataType.UInt64)).Value, "18446744073709551615"),
            "RTU UInt64 scalar lost precision");
        var unsignedValues = await driver.ReadTagAsync("HR0;8", DataType.UInt64);
        TestSupport.Require(unsignedValues.Value is string[] { Length: 2 } unsignedArray
            && unsignedArray.All(value => value == "18446744073709551615"), "RTU UInt64 array lost precision");
    }

    private static async Task VerifyTcpWritesAsync(bool profibus)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accept = listener.AcceptTcpClientAsync(timeout.Token);
        await using IProtocolDriver driver = profibus
            ? new ProfibusDriver(NullLogger<ProfibusDriver>.Instance, NullLogger<ModbusTcpDriver>.Instance)
            : new ModbusTcpDriver(NullLogger<ModbusTcpDriver>.Instance);
        var config = new DeviceConnectionConfig { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        TestSupport.Require((await driver.ConnectAsync(config, timeout.Token)).Success, "TCP fixture connect failed");
        using var socket = await accept;
        foreach (var address in new[] { "HR0;1", "HR0;4" })
            TestSupport.Require(!(await driver.WriteTagAsync(address, DataType.Float, 1.5f, timeout.Token)).Success,
                "Mismatched scalar width was accepted");
        var response = RespondAsync(socket.GetStream(), timeout.Token);
        var written = await driver.WriteTagAsync("HR0;2", DataType.Float, 1.5f, timeout.Token);
        var request = await response;
        TestSupport.Require(written.Success, "Correct scalar width was rejected");
        TestSupport.Require(request.SequenceEqual(new byte[] { 16, 0, 0, 0, 2, 4, 0x3F, 0xC0, 0, 0 }),
            "Rejected writes emitted data or valid Float wrote the wrong width/bytes");
        response = RespondAsync(socket.GetStream(), timeout.Token);
        written = await driver.WriteTagAsync("HR0", DataType.UInt64, "18446744073709551615", timeout.Token);
        request = await response;
        TestSupport.Require(written.Success && request.SequenceEqual(new byte[] { 16, 0, 0, 0, 4, 8 }
            .Concat(Enumerable.Repeat((byte)255, 8))), "UInt64 write lost precision or used wrong width");
        response = RespondAsync(socket.GetStream(), timeout.Token, new byte[] { 3, 8 }.Concat(Enumerable.Repeat((byte)255, 8)).ToArray());
        var scalar = await driver.ReadTagAsync("HR0", DataType.UInt64, timeout.Token);
        await response;
        TestSupport.Require(scalar.Quality == TagQuality.Good && Equals(scalar.Value, "18446744073709551615"),
            "TCP UInt64 scalar lost precision");
        response = RespondAsync(socket.GetStream(), timeout.Token, new byte[] { 3, 16 }.Concat(Enumerable.Repeat((byte)255, 16)).ToArray());
        var array = await driver.ReadTagAsync("HR0;8", DataType.UInt64, timeout.Token);
        await response;
        TestSupport.Require(array.Value is string[] { Length: 2 } unsignedArray
            && unsignedArray.All(value => value == "18446744073709551615"), "TCP UInt64 array lost precision");
    }

    private static async Task<byte[]> RespondAsync(NetworkStream stream, CancellationToken ct, byte[]? response = null)
    {
        var header = new byte[7];
        await stream.ReadExactlyAsync(header, ct);
        var payload = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) - 1];
        await stream.ReadExactlyAsync(payload, ct);
        response ??= payload[..5];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), checked((ushort)(response.Length + 1)));
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(response, ct);
        return payload;
    }

    private static async Task VerifyInovanceAsync()
    {
        IProtocolDriver[] drivers = [
            new InovanceDriver(NullLogger<InovanceDriver>.Instance),
            new InovanceSerialDriver(NullLogger<InovanceSerialDriver>.Instance),
            new InovanceSerialOverTcpDriver(NullLogger<InovanceSerialOverTcpDriver>.Instance),
        ];
        foreach (var driver in drivers)
        {
            await using var disposable = driver;
            var client = DispatchProxy.Create<IInovanceClient, RecordingInovance>();
            var recorder = (RecordingInovance)(object)client;
            SetField(driver, "_client", client);
            SetField(driver, "_state", ConnectionState.Connected);
            SetField(driver, "_series", InovanceSeries.H3U);
            var values = await driver.ReadTagsAsync([
                new() { Address = "invalid", DataType = DataType.UInt16 },
                new() { Address = "D0", DataType = DataType.UInt64 },
            ]);
            TestSupport.Require(values[0].Quality == TagQuality.Bad && values[1].Quality == TagQuality.Good
                && Equals(values[1].Value, "18446744073709551615"), "Inovance lost batch isolation or UInt64 precision");
            TestSupport.Require((await driver.WriteTagAsync("D0", DataType.UInt64, "18446744073709551615")).Success,
                "Inovance UInt64 write failed");
            TestSupport.Require(recorder.LastValue is ulong unsignedValue && unsignedValue == ulong.MaxValue,
                "Inovance UInt64 write was narrowed");
            var before = recorder.Calls;
            foreach (var type in new[] { DataType.Int8, DataType.UInt8 })
            {
                TestSupport.Require((await driver.ReadTagAsync("D0", type)).Quality == TagQuality.Bad, "Unsupported byte read returned Good");
                TestSupport.Require(!(await driver.WriteTagAsync("D0", type, 7)).Success, "Unsupported byte write succeeded");
            }
            TestSupport.Require(recorder.Calls == before, "Unsupported byte operations reached HSL");
        }
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class RecordingRtu : ModbusRtu
    {
        public List<string> Calls { get; } = [];
        public override Task<OperateResult> WriteAsync(string address, bool value)
        { Calls.Add("bool:" + address); return Task.FromResult(OperateResult.CreateSuccessResult()); }
        public override Task<OperateResult> WriteAsync(string address, ushort value)
        { Calls.Add("ushort:" + address); return Task.FromResult(OperateResult.CreateSuccessResult()); }
        public override Task<OperateResult<ushort[]>> ReadUInt16Async(string address, ushort length)
        { Calls.Add($"ushort:{address}:{length}"); return Task.FromResult(OperateResult.CreateSuccessResult(Enumerable.Repeat((ushort)7, length).ToArray())); }
        public override Task<OperateResult<float[]>> ReadFloatAsync(string address, ushort length)
        { Calls.Add($"float:{address}:{length}"); return Task.FromResult(OperateResult.CreateSuccessResult(new float[length])); }
        public override Task<OperateResult<ulong[]>> ReadUInt64Async(string address, ushort length)
        { Calls.Add($"ulong:{address}:{length}"); return Task.FromResult(OperateResult.CreateSuccessResult(Enumerable.Repeat(ulong.MaxValue, length).ToArray())); }
        public override OperateResult<byte[]> ReadFromCoreServer(byte[] command)
        {
            Calls.Add("discrete-command");
            TestSupport.Require(command[1] == 2 && BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(4)) == 9,
                "Discrete input range used wrong function code or count");
            return OperateResult.CreateSuccessResult(new byte[] { 255, 1 });
        }
        public override Task<OperateResult<byte[]>> ReadAsync(string address, ushort length)
        { Calls.Add($"raw:{address}:{length}"); return Task.FromResult(OperateResult.CreateSuccessResult(Enumerable.Repeat((byte)65, length * 2).ToArray())); }
    }

    public class RecordingInovance : DispatchProxy
    {
        public int Calls { get; private set; }
        public object? LastValue { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "Dispose") return null;
            if (method.Name == "ConnectCloseAsync") return Task.CompletedTask;
            Calls++;
            if (method.Name == "ReadUInt64Async")
                return Task.FromResult(OperateResult.CreateSuccessResult(ulong.MaxValue));
            if (method.Name == "WriteAsync" && args![1] is ulong)
            {
                LastValue = args[1];
                return Task.FromResult(OperateResult.CreateSuccessResult());
            }
            throw new InvalidOperationException("Unexpected HSL call: " + method.Name);
        }
    }
}
