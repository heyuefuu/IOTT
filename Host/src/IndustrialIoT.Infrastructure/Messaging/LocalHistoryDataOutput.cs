namespace IndustrialIoT.Infrastructure.Messaging;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Options;

public sealed class LocalHistoryOptions
{
    public const string SectionName = "DataOutput:LocalHistory";
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = Path.Combine(AppContext.BaseDirectory, "App_Data", "collection-history");
    public int RetentionHours { get; set; } = 168;
    public int SegmentBytes { get; set; } = 16 * 1024 * 1024;
    public int MaxRecordBytes { get; set; } = 1024 * 1024;
    public long MaxTotalBytes { get; set; }
}

public sealed record LocalHistoryEntry(string Id, DateTimeOffset RecordedAt, CollectedDataBatch Batch);
public sealed record LocalHistoryPolicy(bool Enabled, int RetentionHours, int SegmentBytes, long MaxTotalBytes);

/// <summary>Independent durable history. MQTT delivery never deletes these records.</summary>
public sealed partial class LocalHistoryDataOutput : IDataOutput, IDisposable
{
    private sealed record StoredRecord(string Id, DateTimeOffset RecordedAt, string Payload);
    private sealed record PendingWrite(string Segment, long Offset, StoredRecord? Record = null, IReadOnlyList<StoredRecord>? Records = null);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly LocalHistoryOptions _options;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root;
    private FileStream? _owner;
    private bool _initialized;
    private int _disposed;
    private IReadOnlyList<string> _lastTransactionIds = [];
    private readonly Dictionary<string, long> _segmentLengths = new(StringComparer.Ordinal);
    private long _storedBytes;
    private DateTimeOffset _nextMaintenance;
    private string? _activeSegment;
    public string Name => "LocalHistory";
    public LocalHistoryPolicy Policy => new(_options.Enabled, _options.RetentionHours, _options.SegmentBytes, _options.MaxTotalBytes);

    public LocalHistoryDataOutput(IOptions<LocalHistoryOptions> options, TimeProvider? clock = null)
    {
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
        if (_options.RetentionHours < 168 || _options.SegmentBytes is < 4096 or > 64 * 1024 * 1024
            || _options.MaxRecordBytes < 512 || _options.MaxRecordBytes > _options.SegmentBytes || _options.MaxTotalBytes < 0)
            throw new ArgumentException("Local history requires retention >=168 hours, valid segment/record sizes and a nonnegative quota.");
        _root = Path.GetFullPath(_options.Directory);
        if (Path.GetPathRoot(_root)?.TrimEnd(Path.DirectorySeparatorChar).Equals(_root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) == true)
            throw new ArgumentException("Local history must use a dedicated directory below a filesystem root.");
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (!_options.Enabled) return;
        await _gate.WaitAsync(ct);
        try { EnsureInitialized(); }
        finally { _gate.Release(); }
    }

    public Task WriteAsync(CollectedDataBatch batch, CancellationToken ct) => WriteManyAsync([batch], ct);

    public Task FlushAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _owner?.Dispose(); _owner = null; _gate.Dispose();
    }
    private static byte[] RecordBytes(StoredRecord record) => Utf8.GetBytes(JsonSerializer.Serialize(record, Json) + "\n");
}
