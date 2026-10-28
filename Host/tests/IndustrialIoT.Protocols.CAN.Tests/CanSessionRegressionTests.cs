namespace IndustrialIoT.Protocols.CAN.Tests;

using IndustrialIoT.Domain.Enums;

internal static partial class CanRegressionTests
{
    private static async Task IdleAcknowledgementsCannotAuthorizeFutureWrite()
    {
        foreach (var reply in new[] { "z", "" })
        {
            var (driver, transport) = await Connected();
            await using var ownedDriver = driver;
            transport.Inject("t1231AA");
            Good(await driver.ReadTagAsync("STD:123", DataType.UInt8), (byte)0xAA);
            transport.Inject(reply);
            await Eventually(() => driver.State == ConnectionState.Faulted,
                "Unsolicited idle acknowledgement did not invalidate the session");
            var sent = transport.Commands.Count;
            var result = await driver.WriteTagAsync("STD:124", DataType.UInt8, (byte)1);
            Require(!result.Success && transport.Commands.Count == sent,
                "Old idle acknowledgement authorized or sent a later frame");
            Bad(await driver.ReadTagAsync("STD:123", DataType.UInt8));
        }
    }
}
