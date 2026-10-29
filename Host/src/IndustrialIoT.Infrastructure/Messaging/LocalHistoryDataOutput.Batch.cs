namespace IndustrialIoT.Infrastructure.Messaging;

using System.Security.Cryptography;
using System.Text.Json;
using IndustrialIoT.Protocols.Models;

public sealed class LocalHistoryWriteException(int committedBatches, Exception inner)
    : IOException("Local history group commit failed: " + inner.Message, inner)
{
    public int CommittedBatches { get; } = committedBatches;
}

public sealed partial class LocalHistoryDataOutput
{
    public async Task WriteManyAsync(IReadOnlyList<CollectedDataBatch> batches, CancellationToken ct)
    {
        if (!_options.Enabled || batches.Count == 0) return;
        if (batches.Count > 1000) throw new ArgumentException("Local history accepts at most 1000 batches per group.");
        var committed = 0;
        await _gate.WaitAsync(ct);
        try
        {
            EnsureInitialized();
            RecoverPending();
            if (_lastTransactionIds.Count <= batches.Count && _lastTransactionIds.SequenceEqual(
                batches.Take(_lastTransactionIds.Count).Select(batch =>
                    Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(JsonSerializer.Serialize(batch, Json)))))))
                committed = _lastTransactionIds.Count;
            while (committed < batches.Count)
            {
                ct.ThrowIfCancellationRequested();
                var recordedAt = _clock.GetUtcNow();
                var records = new List<StoredRecord>();
                var bytes = 0;
                for (var index = committed; index < batches.Count; index++)
                {
                    ArgumentNullException.ThrowIfNull(batches[index]);
                    var payload = JsonSerializer.Serialize(batches[index], Json);
                    var id = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(payload)));
                    var record = new StoredRecord(id, recordedAt, payload);
                    var length = RecordBytes(record).Length;
                    if (length > _options.MaxRecordBytes) throw new IOException("Collection batch exceeds LocalHistory MaxRecordBytes; dispatch is backpressured.");
                    if (bytes + length > _options.SegmentBytes) break;
                    records.Add(record);
                    bytes += length;
                }
                CommitGroup(records, bytes, ct);
                committed += records.Count;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { throw new LocalHistoryWriteException(committed, error); }
        finally { _gate.Release(); }
    }

    private void CommitGroup(IReadOnlyList<StoredRecord> records, int bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_clock.GetUtcNow() >= _nextMaintenance)
        {
            PruneExpired();
            _nextMaintenance = _clock.GetUtcNow().AddMinutes(1);
        }
        if (_options.MaxTotalBytes > 0 && _storedBytes + bytes > _options.MaxTotalBytes)
            throw new IOException("Local history quota is full; unexpired records are retained and dispatch is backpressured.");
        var segment = SelectSegment(bytes, records[0].RecordedAt);
        var pending = new PendingWrite(segment, _segmentLengths.GetValueOrDefault(SafePath(segment)), Records: records);
        SavePending(pending);
        RecoverPending();
    }
}
