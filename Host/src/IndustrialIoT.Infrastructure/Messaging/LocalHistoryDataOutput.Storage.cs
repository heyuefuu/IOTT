namespace IndustrialIoT.Infrastructure.Messaging;

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed partial class LocalHistoryDataOutput
{
    private void EnsureInitialized()
    {
        EnsureSafeRoot();
        Directory.CreateDirectory(_root);
        EnsureSafeRoot();
        if (_initialized) return;
        _owner ??= new FileStream(SafePath(".owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        RecoverPending();
        _segmentLengths.Clear();
        _storedBytes = 0;
        foreach (var segment in EnumerateSegments())
        {
            ReadSegment(segment);
            var length = new FileInfo(segment).Length;
            _segmentLengths.Add(segment, length);
            _storedBytes += length;
        }
        _initialized = true;
    }

    private void EnsureSafeRoot()
    {
        for (DirectoryInfo? directory = new(_root); directory is not null; directory = directory.Parent)
        {
            var attributes = ExistingAttributes(directory.FullName);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Local history paths must not traverse links or junctions.");
        }
    }

    private static FileAttributes ExistingAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
    }

    private string SafePath(string name)
    {
        EnsureSafeRoot();
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name.Contains(':') || name is "." or "..")
            throw new IOException("Unsafe local history filename.");
        var path = Path.Combine(_root, name);
        if ((ExistingAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("Local history does not follow links or use directory entries as files.");
        return path;
    }

    private string[] EnumerateSegments() => Directory.GetFiles(_root, "*.ndjson", SearchOption.TopDirectoryOnly)
        .Where(path => Regex.IsMatch(Path.GetFileName(path), @"^[0-9]{8}-[0-9]{6}\.ndjson$"))
        .Select(path => SafePath(Path.GetFileName(path))).Order(StringComparer.Ordinal).ToArray();

    private string[] Segments() => _segmentLengths.Keys.Order(StringComparer.Ordinal).ToArray();

    private string SelectSegment(int bytes, DateTimeOffset recordedAt)
    {
        var today = recordedAt.ToUniversalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var last = _activeSegment is not null && Path.GetFileName(_activeSegment).StartsWith(today + "-", StringComparison.Ordinal)
            ? _activeSegment : _segmentLengths.Keys.Where(path => Path.GetFileName(path).StartsWith(today + "-", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).LastOrDefault();
        if (last is not null && _segmentLengths.GetValueOrDefault(last) + bytes <= _options.SegmentBytes)
        {
            _activeSegment = last;
            return Path.GetFileName(last);
        }
        var sequence = last is null ? 0 : int.Parse(Path.GetFileName(last).AsSpan(9, 6), CultureInfo.InvariantCulture) + 1;
        if (sequence > 999999) throw new IOException("Local history daily segment limit reached.");
        var name = $"{today}-{sequence:D6}.ndjson";
        _activeSegment = SafePath(name);
        return name;
    }

    private void SavePending(PendingWrite pending)
    {
        var temporary = SafePath(".pending.next");
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, pending, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, SafePath(".pending.json"), overwrite: false);
    }

    private void RecoverPending()
    {
        var path = SafePath(".pending.json");
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > _options.SegmentBytes * 3L) throw new IOException("Oversized local history write journal.");
        var pending = JsonSerializer.Deserialize<PendingWrite>(File.ReadAllText(path, Utf8), Json)
            ?? throw new IOException("Invalid local history write journal.");
        if (!Regex.IsMatch(pending.Segment, @"^[0-9]{8}-[0-9]{6}\.ndjson$") || pending.Offset < 0
            || pending.Offset > _options.SegmentBytes) throw new IOException("Invalid local history write journal target.");
        var records = pending.Records ?? (pending.Record is { } single ? [single] : Array.Empty<StoredRecord>());
        if (records.Count is < 1 or > 1000) throw new IOException("Invalid local history journal group.");
        using var content = new MemoryStream();
        foreach (var record in records)
        {
            Validate(record, pending.Segment);
            var line = RecordBytes(record);
            if (line.Length > _options.MaxRecordBytes) throw new IOException("Oversized local history journal record.");
            content.Write(line);
        }
        var bytes = content.ToArray();
        if (pending.Offset + bytes.Length > _options.SegmentBytes)
            throw new IOException("Oversized local history journal record.");
        using (var stream = new FileStream(SafePath(pending.Segment), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            if (stream.Length < pending.Offset || stream.Length > pending.Offset + bytes.Length)
                throw new IOException("Local history segment no longer matches its pending transaction.");
            stream.Position = pending.Offset;
            var tail = new byte[(int)(stream.Length - pending.Offset)];
            stream.ReadExactly(tail);
            if (!tail.AsSpan().SequenceEqual(bytes.AsSpan(0, tail.Length)))
                throw new IOException("Local history pending tail is corrupt; preserved for diagnosis.");
            if (tail.Length < bytes.Length)
            {
                stream.SetLength(pending.Offset);
                stream.Position = pending.Offset;
                stream.Write(bytes);
            }
            stream.Flush(flushToDisk: true);
        }
        _lastTransactionIds = records.Select(record => record.Id).ToArray();
        var segmentPath = SafePath(pending.Segment);
        var committed = pending.Offset + bytes.Length;
        _storedBytes += committed - _segmentLengths.GetValueOrDefault(segmentPath);
        _segmentLengths[segmentPath] = committed;
        File.Delete(path);
    }

    private static void Validate(StoredRecord record, string segment)
    {
        if (record is null || record.Payload is null || record.Id != Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(record.Payload)))
            || record.RecordedAt.ToUniversalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture) != segment[..8])
            throw new IOException("Local history record checksum or timestamp differs.");
    }
}
