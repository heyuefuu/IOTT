namespace MachineConnectionApi.Tests;

using System.Net;
using MachineConnectionApi.Models;

internal static partial class DeviceUpsertRegressionTests
{
    private static async Task RestoredProbeFailuresDoNotStopOtherDevices()
    {
        foreach (var failure in new Exception[]
        {
            new HttpRequestException("Fixture network failure"),
            new TaskCanceledException("Fixture HTTP timeout", new TimeoutException()),
        })
        {
            var restored = CredentialDevice() with { Id = "failed-restored", RestoredFromUpstream = true, UpstreamSynced = true };
            var pending = CredentialDevice() with { Id = "pending-local", UpstreamSynced = false };
            var store = new MemoryDeviceStore(restored, pending);
            using var handler = new RestoreTestHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/failed-restored", StringComparison.Ordinal))
                    throw failure;
                return Task.FromResult(RestoreReply(null,
                    request.Method == HttpMethod.Get ? HttpStatusCode.NotFound : HttpStatusCode.Created));
            });
            using var client = RestoreTestClient(handler);
            var report = await RestoreService(client, store).SyncAllAsync(CancellationToken.None).WaitAsync(RestoreTestTimeout);
            Expect(report.Failed == 1 && report.Created == 1 && report.Errors.Count == 1 &&
                report.Errors[0].DeviceId == restored.Id, "A failed restored probe must be reported without skipping other devices");
            var rows = store.ReadAll();
            Expect(rows.Single(device => device.Id == restored.Id).UpstreamSynced == false &&
                rows.Single(device => device.Id == pending.Id).UpstreamSynced == true,
                "The failed probe and successful later create must both persist their synchronization status");
        }
    }

    private static async Task RestoreCallerCancellationReleasesOperation()
    {
        var store = new MemoryDeviceStore(
            CredentialDevice() with { Id = "restored", RestoredFromUpstream = true },
            CredentialDevice() with { Id = "pending", UpstreamSynced = false });
        var entered = RestoreSignal();
        var requests = 0;
        using var handler = new RestoreTestHandler(async (request, ct) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                entered.SetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            var status = request.Method == HttpMethod.Post ? HttpStatusCode.Created
                : request.RequestUri!.AbsolutePath.EndsWith("/pending", StringComparison.Ordinal)
                    ? HttpStatusCode.NotFound : HttpStatusCode.OK;
            return RestoreReply(null, status);
        });
        using var client = RestoreTestClient(handler);
        using var caller = new CancellationTokenSource();
        var canceledSync = RestoreService(client, store).SyncAllAsync(caller.Token);
        await entered.Task.WaitAsync(RestoreTestTimeout);
        caller.Cancel();
        await ExpectRestoreCanceled(canceledSync);
        Expect(requests == 1 && store.ReadAll().Single(device => device.Id == "pending").UpstreamSynced == false,
            "Caller cancellation must propagate before processing the next device");
        var next = await RestoreService(client, store).SyncAllAsync(CancellationToken.None).WaitAsync(RestoreTestTimeout);
        Expect(next.Failed == 0 && next.Created == 1 &&
            store.ReadAll().Single(device => device.Id == "pending").UpstreamSynced == true,
            "A canceled synchronization must release the registry operation for a later attempt");
    }

    private static async Task CanceledRestoreWaiterDoesNotBlockLaterOperations()
    {
        var store = new MemoryDeviceStore(CredentialDevice() with { RestoredFromUpstream = true });
        var entered = RestoreSignal();
        var release = RestoreSignal();
        var requests = 0;
        using var handler = new RestoreTestHandler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                entered.SetResult(true);
                await release.Task.WaitAsync(RestoreTestTimeout, ct);
            }
            return RestoreReply(null);
        });
        using var client = RestoreTestClient(handler);
        var owner = RestoreService(client, store).SyncAllAsync(CancellationToken.None);
        await entered.Task.WaitAsync(RestoreTestTimeout);
        using var caller = new CancellationTokenSource();
        var waiter = RestoreService(client, store).SyncAllAsync(caller.Token);
        caller.Cancel();
        try
        {
            await ExpectRestoreCanceled(waiter);
            Expect(requests == 1, "A canceled queued operation must not send upstream requests");
        }
        finally
        {
            release.TrySetResult(true);
        }
        await owner.WaitAsync(RestoreTestTimeout);
        var next = await RestoreService(client, store).SyncAllAsync(CancellationToken.None).WaitAsync(RestoreTestTimeout);
        Expect(next.Failed == 0 && next.Skipped == 1, "Canceling a waiter must not corrupt the active lease or block the queue");
    }

    private static async Task ExpectRestoreCanceled(Task operation)
    {
        var canceled = false;
        try { await operation.WaitAsync(RestoreTestTimeout); }
        catch (OperationCanceledException) { canceled = true; }
        Expect(canceled, "An explicitly canceled caller must receive OperationCanceledException");
    }
}
