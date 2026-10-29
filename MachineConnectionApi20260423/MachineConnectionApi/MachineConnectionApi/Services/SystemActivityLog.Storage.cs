namespace MachineConnectionApi.Services;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MachineConnectionApi.Models;

public sealed partial class SystemActivityLog
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate;
    private readonly string _directory;
    private readonly string _activePath;
    private readonly string _archivePath;
    private readonly TimeProvider _clock;
    public SystemLogRetention Retention { get; }

    public SystemActivityLog() : this(null, null) { }

    public SystemActivityLog(IConfiguration? configuration, TimeProvider? timeProvider = null)
    {
        string? Setting(string name) => configuration?[$"SystemLogs:{name}"]
            ?? Environment.GetEnvironmentVariable($"SystemLogs__{name}");
        static int Number(string? value, int fallback, int minimum, string name) => value is null ? fallback
            : int.TryParse(value, out var parsed) && parsed >= minimum ? parsed
            : throw new ArgumentException($"SystemLogs:{name} must be an integer >= {minimum}.");
        _directory = Path.GetFullPath(Setting("DataDirectory") ?? Path.Combine(AppContext.BaseDirectory, "App_Data"));
        _activePath = Path.Combine(_directory, "system-logs.json");
        _archivePath = Path.Combine(_directory, "system-logs-archive");
        _gate = Gates.GetOrAdd(_activePath, static _ => new object());
        _clock = timeProvider ?? TimeProvider.System;
        Retention = new(Number(Setting("ActiveLimit"), 2000, 1, "ActiveLimit"),
            Number(Setting("ArchiveRetentionDays"), 0, 0, "ArchiveRetentionDays"));
        Directory.CreateDirectory(_directory);
    }

    private static List<SystemLogDto> ReadBatch(string path)
    {
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw new IOException($"日志文件路径被目录占用：{path}");
            return [];
        }
        try
        {
            var rows = JsonSerializer.Deserialize<List<SystemLogDto>>(
                File.ReadAllText(path, new UTF8Encoding(false, true)), JsonOptions)
                ?? throw new JsonException("Log batch must be a JSON array.");
            foreach (var entry in rows) ValidateEntry(entry, path);
            return rows;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidDataException)
        { throw new IOException($"日志文件损坏，请恢复或修复该文件后重试：{path}", error); }
    }

    private static void ValidateEntry(SystemLogDto? entry, string path)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || entry.Type is null || entry.User is null
            || entry.Action is null || entry.Ip is null || entry.Detail is null)
            throw new InvalidDataException($"日志记录字段无效：{path}");
    }

    private static void WriteBatch(string path, IEnumerable<SystemLogDto> rows, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, rows, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
