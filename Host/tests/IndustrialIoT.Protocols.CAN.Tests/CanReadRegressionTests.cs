namespace IndustrialIoT.Protocols.CAN.Tests;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

internal static partial class CanRegressionTests
{
    private static async Task StandardExtendedAndTimestampedFramesStaySeparate()
    {
        var (driver, transport) = await Connected();
        await using var ownedDriver = driver;
        transport.Inject("t1232AABB0123");
        transport.Inject("T00000123211224567");
        Good(await driver.ReadTagAsync("STD:123", DataType.UInt16), (ushort)0xBBAA);
        Good(await driver.ReadTagAsync("EXT:123;endian=BE", DataType.UInt16), (ushort)0x1122);
        Good(await driver.ReadTagAsync("STD:123;endian=BE", DataType.UInt16), (ushort)0xAABB);
        Require(transport.Commands.Count == 3, "Receiving frames must not send poll or RTR commands");
    }

    private static async Task ReceivedTypesAndByteOrder()
    {
        var (driver, transport) = await Connected(Configuration(500));
        await using var ownedDriver = driver;
        (string Frame, string Address, DataType Type, object Value)[] cases =
        [
            ("t100101", "STD:100", DataType.Bool, true),
            ("t1011FE", "STD:101", DataType.Int8, (sbyte)-2),
            ("t1021FF", "STD:102", DataType.UInt8, (byte)255),
            ("t10322EFB", "STD:103", DataType.Int16, (short)-1234),
            ("t1042DCFE", "STD:104", DataType.UInt16, (ushort)0xFEDC),
            ("t1054FEFFFFFF", "STD:105", DataType.Int32, -2),
            ("t1064EFCDAB89", "STD:106", DataType.UInt32, 0x89ABCDEFU),
            ("t1078FEFFFFFFFFFFFFFF", "STD:107", DataType.Int64, -2L),
            ("t1088EFCDAB8967452301", "STD:108", DataType.UInt64, 0x0123456789ABCDEFUL),
            ("t10940000403F", "STD:109", DataType.Float, 0.75f),
            ("t10A8000000000000F83F", "STD:10A", DataType.Double, 1.5d),
            ("t10B3E4B8AD", "STD:10B", DataType.String, "中"),
            ("t10C3AABBCC", "STD:10C", DataType.ByteArray, new byte[] { 0xAA, 0xBB, 0xCC })
        ];
        foreach (var (frame, address, type, value) in cases)
        {
            transport.Inject(frame);
            Good(await driver.ReadTagAsync(address, type), value);
        }
        var batch = await driver.ReadTagsAsync([
            new TagReadRequest { Address = "STD:10C", DataType = DataType.ByteArray },
            new TagReadRequest { Address = "STD:104", DataType = DataType.UInt16 }
        ]);
        Require(batch.Count == 2, "Batch lost requested addresses");
        Good(batch[0], new byte[] { 0xAA, 0xBB, 0xCC });
        Good(batch[1], (ushort)0xFEDC);
        transport.Inject("t110102");
        Bad(await driver.ReadTagAsync("STD:110", DataType.Bool));
        transport.Inject("t1111FF");
        Bad(await driver.ReadTagAsync("STD:111", DataType.String));
        Bad(await driver.ReadTagAsync("STD:111", DataType.Int16));
    }

    private static async Task MalformedAndRemoteFramesAreNotData()
    {
        var (driver, transport) = await Connected();
        await using var ownedDriver = driver;
        foreach (var frame in new[]
        {
            "r1231", "R000001231", "t1239AABB", "t1232AA", "t1231GG", "t1231AA000",
            "t8001AA", "T200000001AA", "t1231AA00000", "t1231AAFFFFextra"
        }) transport.Inject(frame);
        Bad(await driver.ReadTagAsync("STD:123", DataType.UInt8));
        Bad(await driver.ReadTagAsync("EXT:123", DataType.UInt8));
        Require(driver.State == ConnectionState.Connected, "Malformed data must not fabricate a disconnect");
    }

    private static async Task MissingStaleAndDisconnectedDataAreBad()
    {
        var configuration = Configuration();
        configuration.ExtendedProperties["CanFrameMaxAgeMs"] = "20";
        var (driver, transport) = await Connected(configuration);
        await using var ownedDriver = driver;
        Bad(await driver.ReadTagAsync("STD:123", DataType.UInt8));
        transport.Inject("t1231AA");
        Good(await driver.ReadTagAsync("STD:123", DataType.UInt8), (byte)0xAA);
        await Task.Delay(40);
        Bad(await driver.ReadTagAsync("STD:123", DataType.UInt8));
        transport.Inject("t1231BB");
        Good(await driver.ReadTagAsync("STD:123", DataType.UInt8), (byte)0xBB);
        await driver.DisconnectAsync();
        Bad(await driver.ReadTagAsync("STD:123", DataType.UInt8));
        Require(driver.State == ConnectionState.Disconnected, "Disconnect did not update state");
    }

    private static async Task DelayedFreshFrameCompletesWaitingRead()
    {
        var (driver, transport) = await Connected();
        await using var ownedDriver = driver;
        var reading = driver.ReadTagAsync("EXT:18FF50E5", DataType.UInt16);
        transport.Inject("t12321122");
        transport.Inject("T18FF50E52AABB");
        Good(await reading, (ushort)0xBBAA);
        foreach (var address in new[] { "STD:800", "EXT:20000000", "STD:123;endian=XX", "123", "STD:123;offset=1" })
            Bad(await driver.ReadTagAsync(address, DataType.UInt8));
    }
}
