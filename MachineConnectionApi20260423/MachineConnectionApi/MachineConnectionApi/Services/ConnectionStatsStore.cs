namespace MachineConnectionApi.Services;

/// <summary>单次连接测试记录（内存态，按设备保留最近 24h/最多 2000 条）。</summary>
public sealed record ConnectionStatEntry(DateTimeOffset Timestamp, bool Success, long LatencyMs, string Mode);

public sealed record ConnectionStatsSummary(
    int Total,
    int Success,
    int Failure,
    double SuccessRate,
    double AvgLatencyMs,
    long MaxLatencyMs,
    int DisconnectEvents,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    int WindowHours);

/// <summary>
/// 连接测试结果统计（供 24h 连接成功率/平均响应/断线次数报表）。
/// 说明：内存态，进程重启后清零；如需跨重启持久化可后续接入 JsonFileStore。
/// </summary>
public sealed class ConnectionStatsStore
{
    private const int MaxPerDevice = 2000;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private readonly object _gate = new();
    private readonly Dictionary<string, List<ConnectionStatEntry>> _entries = new(StringComparer.Ordinal);

    public void Record(string deviceId, bool success, long latencyMs, string mode)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        var entry = new ConnectionStatEntry(DateTimeOffset.Now, success, latencyMs, mode);
        lock (_gate)
        {
            if (!_entries.TryGetValue(deviceId, out var list))
            {
                list = [];
                _entries[deviceId] = list;
            }
            list.Add(entry);
            var cutoff = DateTimeOffset.Now - Retention;
            list.RemoveAll(e => e.Timestamp < cutoff);
            if (list.Count > MaxPerDevice)
                list.RemoveRange(0, list.Count - MaxPerDevice);
        }
    }

    public ConnectionStatsSummary Summarize(string deviceId, int hours)
    {
        var window = Math.Clamp(hours, 1, 24 * 30);
        var cutoff = DateTimeOffset.Now.AddHours(-window);

        List<ConnectionStatEntry> snapshot;
        lock (_gate)
        {
            snapshot = _entries.TryGetValue(deviceId, out var list)
                ? list.Where(e => e.Timestamp >= cutoff).ToList()
                : [];
        }

        var total = snapshot.Count;
        var successes = snapshot.Count(e => e.Success);
        var failures = total - successes;
        var latencies = snapshot.Where(e => e.Success && e.LatencyMs > 0).Select(e => e.LatencyMs).ToList();

        var disconnects = 0;
        for (var i = 1; i < snapshot.Count; i++)
            if (snapshot[i - 1].Success && !snapshot[i].Success)
                disconnects++;

        return new ConnectionStatsSummary(
            total,
            successes,
            failures,
            total == 0 ? 0 : Math.Round(100.0 * successes / total, 2),
            latencies.Count == 0 ? 0 : Math.Round(latencies.Average(), 1),
            latencies.Count == 0 ? 0 : latencies.Max(),
            disconnects,
            snapshot.LastOrDefault(e => e.Success)?.Timestamp,
            snapshot.LastOrDefault(e => !e.Success)?.Timestamp,
            window);
    }
}
