using System.Diagnostics;
using System.Net;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

public sealed partial class CsConnectivityService
{
    private readonly object _parallelTargetsGate = new();
    private readonly HashSet<(string Host, int Port)> _activeParallelTargets = new();
    private TaskCompletionSource _parallelTargetsChanged =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static (string Host, int Port) NormalizeParallelTestTarget(string? host, int port)
    {
        var normalized = (host ?? "").Trim().TrimEnd('.');
        if (IPAddress.TryParse(normalized, out var address))
            normalized = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        return (normalized.ToLowerInvariant(), port);
    }

    private static (string Host, int Port)[] GetParallelTestTargets(CsParallelTestRequest request)
    {
        if (IsMqtt(request.Protocol) || !TryParseIpv4(request.StartIp, out var startValue))
            return new[] { NormalizeParallelTestTarget(request.StartIp, request.Port) };
        return Enumerable.Range(0, Math.Clamp(request.DeviceCount, 1, MaxDeviceCount))
            .Select(index => NormalizeParallelTestTarget(UIntToIpv4(startValue + (uint)index), request.Port))
            .ToArray();
    }

    private async Task AcquireParallelTestTargetsAsync(
        (string Host, int Port)[] targets, TimeSpan wait, CancellationToken ct)
    {
        var waitMilliseconds = (long)wait.TotalMilliseconds;
        if (waitMilliseconds is < -1 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(wait));
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            Task changed;
            lock (_parallelTargetsGate)
            {
                ct.ThrowIfCancellationRequested();
                // Reserve the entire range atomically; overlapping requests never hold partial ranges.
                if (!targets.Any(_activeParallelTargets.Contains))
                {
                    _activeParallelTargets.UnionWith(targets);
                    return;
                }
                changed = _parallelTargetsChanged.Task;
            }

            var remaining = waitMilliseconds == -1
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromMilliseconds(Math.Max(0, waitMilliseconds - stopwatch.Elapsed.TotalMilliseconds));
            if (remaining == TimeSpan.Zero) throw new CsParallelTestBusyException();
            try { await changed.WaitAsync(remaining, ct); }
            catch (TimeoutException) { throw new CsParallelTestBusyException(); }
        }
    }

    private void ReleaseParallelTestTargets((string Host, int Port)[] targets)
    {
        TaskCompletionSource changed;
        lock (_parallelTargetsGate)
        {
            _activeParallelTargets.ExceptWith(targets);
            changed = _parallelTargetsChanged;
            _parallelTargetsChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
    }
}
