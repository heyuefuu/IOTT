namespace IndustrialIoT.Protocols.CAN.Tests;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class CanRegressionTests
{
    private static async Task Main()
    {
        await ConnectionCommandsAndStatus();
        await InitialClosedChannelBellIsAccepted();
        await ConfigurationFailuresDoNotOpenTransport();
        await StandardExtendedAndTimestampedFramesStaySeparate();
        await ReceivedTypesAndByteOrder();
        await MalformedAndRemoteFramesAreNotData();
        await MissingStaleAndDisconnectedDataAreBad();
        await DelayedFreshFrameCompletesWaitingRead();
        await WritesUseActualSlcanCommands();
        await NumericAndTextWritesRoundTripOnTheWire();
        await InvalidWritesNeverReachAdapter();
        await WriteAcknowledgementIsNotReadback();
        await BellRejectsWriteWithoutInventingSuccess();
        await CommandTimeoutFaultsAndClearsCache();
        await CancelledCommandFaultsAndRejectsLateReply();
        await TransportFailureFaultsAndClearsCache();
        await ConcurrentCommandsAndReadsRemainCorrelated();
        await DisconnectUnblocksPendingRead();
        await DisposeUnblocksPendingRead();
        await InitializationErrorsDoNotLeaveAnOpenSession();
        await ConnectionCancellationDisposesTransport();
        await WrongFrameAcknowledgementFaultsSession();
        await IdleAcknowledgementsCannotAuthorizeFutureWrite();
        await ReconnectWaitsForCancelledOldCommand();
        Console.WriteLine("All CAN SLCAN regression tests passed (24 cases).");
    }

    private static DeviceConnectionConfig Configuration(int timeoutMs = 150) => new()
    {
        Host = "serial", Port = 0,
        ConnectTimeout = TimeSpan.FromSeconds(1),
        ReadTimeout = TimeSpan.FromMilliseconds(timeoutMs),
        ExtendedProperties = new()
        {
            ["CanTransport"] = "SLCAN", ["PortName"] = "COM3",
            ["BaudRate"] = "115200", ["CanBitRate"] = "500000"
        }
    };

    private static async Task<(CanDriver Driver, SlcanTestTransport Transport)> Connected(
        DeviceConnectionConfig? configuration = null, Action<SlcanTestTransport>? prepare = null)
    {
        var transport = new SlcanTestTransport();
        prepare?.Invoke(transport);
        var driver = new CanDriver(NullLogger<CanDriver>.Instance, transport);
        var result = await driver.ConnectAsync(configuration ?? Configuration());
        Require(result.Success, $"Connect failed: {result.ErrorMessage}");
        Require(driver.State == ConnectionState.Connected, "Successful connect did not set Connected");
        return (driver, transport);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Good(TagValue result, object expected)
    {
        var equal = expected is byte[] bytes
            ? result.Value is byte[] actual && bytes.SequenceEqual(actual)
            : Equals(result.Value, expected);
        Require(result.Quality == TagQuality.Good && equal,
            $"{result.Address}: expected {expected} ({expected.GetType().Name}), got {result.Value} ({result.Quality}): {result.ErrorMessage}");
    }

    private static void Bad(TagValue result) => Require(result.Quality == TagQuality.Bad
        && !string.IsNullOrWhiteSpace(result.ErrorMessage), $"{result.Address}: invalid read returned usable data");

    private static async Task Eventually(Func<bool> condition, string message)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            if (deadline.IsCancellationRequested) throw new InvalidOperationException(message);
            await Task.Delay(5);
        }
    }
}
