namespace IndustrialIoT.Protocols.CAN.Tests;

using IndustrialIoT.Domain.Enums;

internal static partial class CanRegressionTests
{
    private static async Task ReconnectWaitsForCancelledOldCommand()
    {
        var (driver, transport) = await Connected(Configuration(2000));
        await using var ownedDriver = driver;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeReply = async (command, token) =>
        {
            if (command != "t1231AA") return;
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                await release.Task;
                throw;
            }
        };
        var writing = driver.WriteTagAsync("STD:123", DataType.UInt8, (byte)0xAA);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        transport.Fail();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var disconnecting = driver.DisconnectAsync();
        await transport.DisposeObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var connecting = driver.ConnectAsync(Configuration(1000));
        try
        {
            var completed = await Task.WhenAny(connecting, Task.Delay(100));
            Require(completed != connecting && transport.OpenCount == 1 && !disconnecting.IsCompleted,
                "New session opened before the cancelled old command left its controlled barrier");
        }
        finally { release.TrySetResult(); }
        Require(!(await writing).Success, "Interrupted old-session write succeeded");
        await disconnecting.WaitAsync(TimeSpan.FromSeconds(1));
        var result = await connecting.WaitAsync(TimeSpan.FromSeconds(2));
        Require(result.Success && transport.OpenCount == 2, "Reconnect failed after draining the old command");
        transport.BeforeReply = null;
        Require(await driver.PingAsync() && driver.State == ConnectionState.Connected,
            "Old command completion faulted the new session");
        transport.Inject("t1241BB");
        Good(await driver.ReadTagAsync("STD:124", DataType.UInt8), (byte)0xBB);
    }
}
