namespace IndustrialIoT.Protocols.CAN.Tests;

using IndustrialIoT.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class CanRegressionTests
{
    private static async Task ConnectionCommandsAndStatus()
    {
        var (driver, transport) = await Connected();
        await using var ownedDriver = driver;
        Require(transport.OpenedConfiguration?.ExtendedProperties["PortName"] == "COM3", "Serial settings were not passed through");
        Require(transport.Commands.SequenceEqual(new[] { "C", "S6", "O" }), "Initialization must close, configure 500k and open in order");
        Require(await driver.PingAsync(), "F00 should report a healthy adapter");
        Require(transport.Commands.Last() == "F", "Ping must read actual CAN adapter status");
        transport.Reply = command => command == "F" ? "F80" : SlcanTestTransport.DefaultReply(command);
        Require(!await driver.PingAsync(), "Nonzero CAN status must not report healthy");
        Require(driver.State == ConnectionState.Connected, "Nonzero status alone need not disconnect transport");
    }

    private static async Task InitialClosedChannelBellIsAccepted()
    {
        var (driver, transport) = await Connected(prepare: peer => peer.Reply = command =>
            command == "C" ? "\a" : SlcanTestTransport.DefaultReply(command));
        await using var ownedDriver = driver;
        Require(transport.Commands.Take(3).SequenceEqual(new[] { "C", "S6", "O" }), "Initial closed-channel BEL prevented setup");
    }

    private static async Task ConfigurationFailuresDoNotOpenTransport()
    {
        (string Key, string? Value)[] invalid =
        [
            ("CanTransport", "CANOPEN"), ("PortName", null), ("PortName", ""),
            ("BaudRate", "0"), ("BaudRate", "not-a-number"),
            ("CanBitRate", "123456"), ("CanFrameMaxAgeMs", "0")
        ];
        foreach (var (key, value) in invalid)
        {
            var configuration = Configuration();
            if (value is null) configuration.ExtendedProperties.Remove(key);
            else configuration.ExtendedProperties[key] = value;
            if (key == "PortName" && value is null) configuration = configuration with { Host = "" };
            var transport = new SlcanTestTransport();
            await using var driver = new CanDriver(NullLogger<CanDriver>.Instance, transport);
            var result = await driver.ConnectAsync(configuration);
            Require(!result.Success && !string.IsNullOrWhiteSpace(result.ErrorMessage), $"Invalid {key} was accepted");
            Require(transport.OpenedConfiguration is null && transport.Commands.Count == 0,
                $"Invalid {key} reached the serial transport");
        }
    }
}
