namespace IndustrialIoT.Protocols.CAN.Tests;

using IndustrialIoT.Domain.Enums;

internal static partial class CanRegressionTests
{
    private static async Task CommandTimeoutFaultsAndClearsCache()
    {
        var (driver, transport) = await Connected();
        await using var ownedDriver = driver;
        transport.Inject("t1231AA");
        Good(await driver.ReadTagAsync("STD:123", DataType.UInt8), (byte)0xAA);
        transport.Reply = _ => null;
        var result = await driver.WriteTagAsync("STD:123", DataType.UInt8, (byte)0xBB);
        Require(!result.Success, "Missing command acknowledgement must fail the write");
        await Eventually(() => driver.State == ConnectionState.Faulted, "Command timeout did not fault the session");
        Bad(await driver.ReadTagAsync("STD:123", DataType.UInt8));
    }

    private static async Task CancelledCommandFaultsAndRejectsLateReply()
    {
        var (driver, transport) = await Connected(Configuration(1000));
        await using var ownedDriver = driver;
        transport.Inject("t1231AA");
        Good(await driver.ReadTagAsync("STD:123", DataType.UInt8), (byte)0xAA);
        transport.Reply = _ => null;
        using var cancellation = new CancellationTokenSource();
        var writing = driver.WriteTagAsync("STD:123", DataType.UInt8, (byte)0xBB, cancellation.Token);
        await Eventually(() => transport.Commands.Count == 4, "Cancelled write was never sent");
        cancellation.Cancel();
        try { Require(!(await writing).Success, "Cancelled command succeeded"); }
        catch (OperationCanceledException) { }
        await Eventually(() => driver.State == ConnectionState.Faulted, "Issued command cancellation did not fault session");
        if (!transport.Disposed) transport.Inject("z");
        Bad(await driver.ReadTagAsync("STD:123", DataType.UInt8));
        Require(!(await driver.WriteTagAsync("STD:124", DataType.UInt8, (byte)1)).Success,
            "Late acknowledgement was consumed by a later command");
        Require(transport.Commands.Count == 4, "Faulted session sent another command");
    }

    private static async Task TransportFailureFaultsAndClearsCache()
    {
        var (driver, transport) = await Connected();
        await using var ownedDriver = driver;
        transport.Inject("t1231AA");
        Good(await driver.ReadTagAsync("STD:123", DataType.UInt8), (byte)0xAA);
        transport.Fail();
        await Eventually(() => driver.State == ConnectionState.Faulted, "Serial receive failure did not fault session");
        Bad(await driver.ReadTagAsync("STD:123", DataType.UInt8));
    }

    private static async Task ConcurrentCommandsAndReadsRemainCorrelated()
    {
        var (driver, transport) = await Connected(Configuration(1000));
        await using var ownedDriver = driver;
        transport.Reply = command => command == "t1231AA" ? null : SlcanTestTransport.DefaultReply(command);
        var first = driver.WriteTagAsync("STD:123", DataType.UInt8, (byte)0xAA);
        await Eventually(() => transport.Commands.Count == 4, "First concurrent command was not sent");
        var second = driver.WriteTagAsync("EXT:18FF50E5", DataType.UInt8, (byte)0xBB);
        var ping = driver.PingAsync();
        var read = driver.ReadTagAsync("STD:125", DataType.UInt16);
        transport.Inject("t12521122");
        Good(await read, (ushort)0x2211);
        await Task.Delay(20);
        Require(transport.Commands.Count == 4, "Commands were not serialized while acknowledgement was pending");
        transport.Inject("z");
        Require((await first).Success && (await second).Success && await ping,
            "Concurrent command acknowledgements became crossed or blocked received data");
        var commands = transport.Commands.Skip(3).ToArray();
        Require(commands.Length == 3 && commands.Contains("t1231AA")
            && commands.Contains("T18FF50E51BB") && commands.Contains("F"), "Concurrent commands were duplicated or lost");
    }
}
