namespace IndustrialIoT.Protocols.CAN.Tests;

using IndustrialIoT.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class CanRegressionTests
{
    private static async Task InitializationErrorsDoNotLeaveAnOpenSession()
    {
        foreach (var failure in new[] { (Command: "S6", Reply: "\a"), (Command: "O", Reply: "unexpected") })
        {
            var transport = new SlcanTestTransport
            {
                Reply = command => command == failure.Command ? failure.Reply : SlcanTestTransport.DefaultReply(command)
            };
            await using var driver = new CanDriver(NullLogger<CanDriver>.Instance, transport);
            var result = await driver.ConnectAsync(Configuration());
            Require(!result.Success && !string.IsNullOrWhiteSpace(result.ErrorMessage), "Invalid initialization acknowledgement was accepted");
            Require(driver.State == ConnectionState.Faulted && transport.Disposed, "Failed initialization leaked its session");
        }
    }

    private static async Task ConnectionCancellationDisposesTransport()
    {
        var transport = new SlcanTestTransport
        {
            Reply = command => command == "S6" ? null : SlcanTestTransport.DefaultReply(command)
        };
        await using var driver = new CanDriver(NullLogger<CanDriver>.Instance, transport);
        using var cancellation = new CancellationTokenSource();
        var connecting = driver.ConnectAsync(Configuration(1000), cancellation.Token);
        await Eventually(() => transport.Commands.Count == 2, "Connection did not start adapter initialization");
        cancellation.Cancel();
        try { Require(!(await connecting).Success, "Cancelled connection succeeded"); }
        catch (OperationCanceledException) { }
        Require(driver.State == ConnectionState.Faulted && transport.Disposed, "Connection cancellation did not close the transport");
    }

    private static async Task WrongFrameAcknowledgementFaultsSession()
    {
        foreach (var test in new[] { (Address: "STD:123", WrongReply: "Z"), (Address: "EXT:18FF50E5", WrongReply: "z") })
        {
            var (driver, transport) = await Connected();
            await using var ownedDriver = driver;
            transport.Reply = _ => test.WrongReply;
            var result = await driver.WriteTagAsync(test.Address, DataType.UInt8, (byte)1);
            Require(!result.Success, "Wrong standard/extended acknowledgement completed the write");
            Require(driver.State == ConnectionState.Faulted, "Mismatched acknowledgement did not invalidate the session");
        }
    }
}
