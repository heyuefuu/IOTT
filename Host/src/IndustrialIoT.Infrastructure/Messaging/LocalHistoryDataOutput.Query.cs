namespace IndustrialIoT.Infrastructure.Messaging;

using System.Globalization;
using System.Text;
using System.Text.Json;
using IndustrialIoT.Protocols.Models;

public sealed partial class LocalHistoryDataOutput
{
    public async Task<IReadOnlyList<LocalHistoryEntry>> QueryAsync(string deviceId, DateTimeOffset? from = null,
        DateTimeOffset? to = null, int limit = 1000, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || limit is < 1 or > 1000 || from > to)
            throw new ArgumentException("A deviceId, ordered date range and limit 1..1000 are required.");
        if (!_options.Enabled) throw new InvalidOperationException("Local history is disabled.");
        KeyValuePair<string, long>[] snapshot;
        await _gate.WaitAsync(ct);
        try
        {
            EnsureInitialized();
            RecoverPending();
            snapshot = _segmentLengths.OrderBy(pair => pair.Key, StringComparer.Ordinal).Reverse().ToArray();
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { throw new IOException("Local history JSON/UTF-8 is corrupt; files have been preserved.", error); }
        finally { _gate.Release(); }
        try
        {
            var rows = new List<LocalHistoryEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var segment in snapshot)
            {
                var records = await ReadSnapshotAsync(segment.Key, segment.Value, ct);
                foreach (var stored in records.Reverse())
                {
                    ct.ThrowIfCancellationRequested();
                    var batch = JsonSerializer.Deserialize<CollectedDataBatch>(stored.Payload, Json)
                        ?? throw new IOException("Local history batch is missing.");
                    if (batch.DeviceId != deviceId || from is { } start && batch.CollectedAt < start
                        || to is { } end && batch.CollectedAt > end || !seen.Add(stored.Id)) continue;
                    rows.Add(new(stored.Id, stored.RecordedAt, batch));
                    if (rows.Count == limit) return rows;
                }
            }
            return rows;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { throw new IOException("Local history JSON/UTF-8 is corrupt; files have been preserved.", error); }
    }

    private IReadOnlyList<StoredRecord> ReadSegment(string path)
    {
        path = SafePath(Path.GetFileName(path));
        if (new FileInfo(path).Length > _options.SegmentBytes) throw new IOException("Local history segment exceeds configured bound.");
        return ParseRecords(path, File.ReadAllText(path, Utf8), default);
    }

    private IReadOnlyList<StoredRecord> ParseRecords(string path, string text, CancellationToken ct)
    {
        if (text.Length > 0 && text[^1] != '\n') throw new IOException("Local history has an incomplete record without a write journal.");
        var records = new List<StoredRecord>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            ct.ThrowIfCancellationRequested();
            var record = JsonSerializer.Deserialize<StoredRecord>(line, Json) ?? throw new IOException("Local history record is missing.");
            Validate(record, Path.GetFileName(path));
            records.Add(record);
        }
        return records;
    }

    private async Task<IReadOnlyList<StoredRecord>> ReadSnapshotAsync(string path, long length, CancellationToken ct)
    {
        path = SafePath(Path.GetFileName(path));
        if (length < 0 || length > _options.SegmentBytes) throw new IOException("Invalid committed history segment length.");
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytes = new byte[(int)length];
            await stream.ReadExactlyAsync(bytes, ct);
            return ParseRecords(path, Utf8.GetString(bytes), ct);
        }
        catch (FileNotFoundException error)
        { throw new IOException("History retention changed during the query; retry to obtain a new snapshot.", error); }
    }

    private void PruneExpired()
    {
        var cutoff = _clock.GetUtcNow().AddHours(-_options.RetentionHours);
        foreach (var path in Segments())
        {
            var day = DateTimeOffset.ParseExact(Path.GetFileName(path)[..8], "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            // Delete only full UTC days older than the rolling retention window.
            if (day.AddDays(1) > cutoff) continue;
            ReadSegment(path);
            File.Delete(SafePath(Path.GetFileName(path)));
            _storedBytes -= _segmentLengths[path];
            _segmentLengths.Remove(path);
        }
    }
}
