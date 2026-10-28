namespace IndustrialIoT.Protocols.CAN.Tests;

using IndustrialIoT.Domain.Enums;

internal static partial class CanRegressionTests
{
    private static async Task WritesUseActualSlcanCommands()
    {
        var (driver, transport) = await Connected();
        await using var ownedDriver = driver;
        var standard = await driver.WriteTagAsync("STD:123", DataType.ByteArray, new byte[] { 0xAA, 0xBB });
        var extended = await driver.WriteTagAsync("EXT:18FF50E5", DataType.ByteArray, new byte[] { 0xAA, 0xBB });
        Require(standard.Success && extended.Success, "z/Z acknowledgements must complete matching writes");
        Require(transport.Commands.Skip(3).SequenceEqual(new[] { "t1232AABB", "T18FF50E52AABB" }),
            "Standard/extended frame wire commands differ from SLCAN");
        transport.Reply = _ => "";
        Require((await driver.WriteTagAsync("STD:123", DataType.UInt8, (byte)1)).Success,
            "Classic empty-CR acknowledgement must be accepted");
    }

    private static async Task NumericAndTextWritesRoundTripOnTheWire()
    {
        var (driver, transport) = await Connected();
        await using var ownedDriver = driver;
        (DataType Type, object Value, string Bytes)[] cases =
        [
            (DataType.Bool, true, "01"), (DataType.Int8, (sbyte)-2, "FE"),
            (DataType.UInt8, (byte)255, "FF"), (DataType.Int16, (short)-1234, "2EFB"),
            (DataType.UInt16, (ushort)0xFEDC, "DCFE"), (DataType.Int32, -2, "FEFFFFFF"),
            (DataType.UInt32, 0x89ABCDEFU, "EFCDAB89"), (DataType.Int64, -2L, "FEFFFFFFFFFFFFFF"),
            (DataType.UInt64, 0x0123456789ABCDEFUL, "EFCDAB8967452301"),
            (DataType.Float, 0.75f, "0000403F"), (DataType.Double, 1.5d, "000000000000F83F"),
            (DataType.String, "中", "E4B8AD"), (DataType.ByteArray, new byte[] { 0xAA, 0xBB }, "AABB")
        ];
        foreach (var (type, value, bytes) in cases)
        {
            var result = await driver.WriteTagAsync("STD:123", type, value);
            Require(result.Success, $"{type}: {result.ErrorMessage}");
            Require(transport.Commands.Last() == $"t123{bytes.Length / 2}{bytes}", $"{type}: wrong little-endian payload");
        }
        var bigEndian = await driver.WriteTagAsync("STD:123;endian=BE", DataType.UInt32, 0x89ABCDEFU);
        Require(bigEndian.Success && transport.Commands.Last() == "t123489ABCDEF", "Big-endian write did not reverse numeric bytes");
    }

    private static async Task InvalidWritesNeverReachAdapter()
    {
        var (driver, transport) = await Connected();
        await using var ownedDriver = driver;
        (string Address, DataType Type, object Value)[] invalid =
        [
            ("STD:800", DataType.UInt8, 1), ("EXT:20000000", DataType.UInt8, 1),
            ("STD:123;endian=XX", DataType.UInt8, 1), ("STD:123;offset=1", DataType.UInt8, 1),
            ("STD:123", DataType.UInt8, 256), ("STD:123", DataType.Int8, -129),
            ("STD:123", DataType.UInt16, -1), ("STD:123", DataType.Bool, 2),
            ("STD:123", DataType.String, "123456789"), ("STD:123", DataType.String, "中文汉"),
            ("STD:123", DataType.ByteArray, new byte[9]), ("STD:123", (DataType)999, 1),
            ("STD:123\rO", DataType.UInt8, 1), ("STD:123", DataType.String, "\ud800")
        ];
        foreach (var (address, type, value) in invalid)
        {
            var result = await driver.WriteTagAsync(address, type, value);
            Require(!result.Success && !string.IsNullOrWhiteSpace(result.ErrorMessage),
                $"Invalid write succeeded: {address} {type} {value}");
        }
        Require(transport.Commands.Count == 3, "Invalid requests must not reach the CAN adapter");
    }

    private static async Task WriteAcknowledgementIsNotReadback()
    {
        var (driver, transport) = await Connected();
        await using var ownedDriver = driver;
        Require((await driver.WriteTagAsync("STD:123", DataType.UInt8, (byte)0xAA)).Success,
            "Fixture adapter did not acknowledge the frame");
        Bad(await driver.ReadTagAsync("STD:123", DataType.UInt8));
        transport.Inject("t1231BB");
        Good(await driver.ReadTagAsync("STD:123", DataType.UInt8), (byte)0xBB);
        Require((await driver.WriteTagAsync("STD:123", DataType.UInt8, (byte)0xCC)).Success,
            "Second fixture write failed");
        Good(await driver.ReadTagAsync("STD:123", DataType.UInt8), (byte)0xBB);
    }

    private static async Task BellRejectsWriteWithoutInventingSuccess()
    {
        var (driver, transport) = await Connected();
        await using var ownedDriver = driver;
        transport.Reply = _ => "\a";
        var result = await driver.WriteTagAsync("STD:123", DataType.UInt8, (byte)0xAA);
        Require(!result.Success && !string.IsNullOrWhiteSpace(result.ErrorMessage), "BEL must reject transmit");
        Require(driver.State == ConnectionState.Connected, "A correlated BEL must not force a reconnect");
        transport.Reply = SlcanTestTransport.DefaultReply;
        Require((await driver.WriteTagAsync("STD:123", DataType.UInt8, (byte)0xBB)).Success,
            "Write could not recover after explicit adapter rejection");
    }
}
