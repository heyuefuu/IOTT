namespace MachineConnectionApi.Services;

using System.Runtime.CompilerServices;

internal static class DeviceRegistryGate
{
    // The controller and reconciliation service share one store; the lease covers both local and upstream changes.
    private static readonly ConditionalWeakTable<IDeviceStore, SemaphoreSlim> Gates = new();

    public static async Task<IDisposable> EnterAsync(IDeviceStore store, CancellationToken ct)
    {
        var gate = Gates.GetValue(store, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
