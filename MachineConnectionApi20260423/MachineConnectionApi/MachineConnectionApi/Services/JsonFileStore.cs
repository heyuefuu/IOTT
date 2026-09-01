namespace MachineConnectionApi.Services;

using System.Collections.Concurrent;
using System.Text.Json;

public sealed class JsonFileStore<T> where T : class
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate;
    private readonly string _path;

    public JsonFileStore(string fileName)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "App_Data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, fileName);
        _gate = Gates.GetOrAdd(_path, static _ => new object());
    }

    public List<T> ReadAll()
    {
        lock (_gate) return ReadAllCore();
    }

    public void WriteAll(IEnumerable<T> items)
    {
        lock (_gate) WriteAllCore(items);
    }

    public TResult Update<TResult>(Func<List<T>, TResult> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            var rows = ReadAllCore();
            var before = JsonSerializer.Serialize(rows, JsonOptions);
            var result = update(rows);
            var after = JsonSerializer.Serialize(rows, JsonOptions);
            // 回调未产生实际变更（如查询未命中、幂等操作）时跳过磁盘写。
            if (!string.Equals(before, after, StringComparison.Ordinal))
                WriteAllCore(after);
            return result;
        }
    }

    private List<T> ReadAllCore()
    {
        if (!File.Exists(_path)) return [];
        var json = File.ReadAllText(_path);
        return string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? [];
    }

    private void WriteAllCore(IEnumerable<T> items) =>
        WriteAllCore(JsonSerializer.Serialize(items.ToList(), JsonOptions));

    private void WriteAllCore(string json)
    {
        var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
