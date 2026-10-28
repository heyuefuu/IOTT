namespace IndustrialIoT.Protocols.CAN.Tests;

using IndustrialIoT.Domain.Enums;

internal static partial class CanRegressionTests
{
    private static async Task DisconnectUnblocksPendingRead()
    {
        var (driver, transport) = await Connected(Configuration(3000));
        await using var ownedDriver = driver;
        var pending = driver.ReadTagAsync("STD:123", DataType.UInt8);
        await driver.DisconnectAsync();
        Bad(await pending.WaitAsync(TimeSpan.FromSeconds(1)));
        Require(driver.State == ConnectionState.Disconnected && transport.Disposed,
            "Disconnect must dispose serial transport and unblock readers");
    }

    private static async Task DisposeUnblocksPendingRead()
    {
        var (driver, transport) = await Connected(Configuration(3000));
        var pending = driver.ReadTagAsync("EXT:18FF50E5", DataType.UInt16);
        await driver.DisposeAsync();
        Bad(await pending.WaitAsync(TimeSpan.FromSeconds(1)));
        Require(driver.State == ConnectionState.Disconnected && transport.Disposed,
            "Dispose must close the serial transport and unblock readers");
        await driver.DisposeAsync();
    }
}
