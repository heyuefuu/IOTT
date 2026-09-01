namespace MachineConnectionApi.Tests;

using System.Text.Json;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;

internal static class JsonFileStoreRegressionTests
{
    public static async Task ConcurrentUpdatesDoNotLoseRows()
    {
        var fileName = $"json-store-regression-{Guid.NewGuid():N}.json";
        var directory = Path.Combine(AppContext.BaseDirectory, "App_Data");
        var path = Path.Combine(directory, fileName);
        var first = new JsonFileStore<SystemLogDto>(fileName);
        var second = new JsonFileStore<SystemLogDto>(fileName);
        try
        {
            first.WriteAll([]);
            var writes = Enumerable.Range(0, 100).Select(index => Task.Run(() =>
                (index % 2 == 0 ? first : second).Update(rows =>
                {
                    rows.Add(new SystemLogDto
                    {
                        Id = index.ToString(), Type = "system", User = "test",
                        Action = "atomic-update", Ip = "-", Timestamp = DateTimeOffset.Now,
                        Detail = index.ToString(),
                    });
                    return 0;
                })));
            await Task.WhenAll(writes);
            var rows = first.ReadAll();
            if (rows.Count != 100 || rows.Select(x => x.Id).Distinct().Count() != 100)
                throw new InvalidOperationException($"Atomic update lost rows: {rows.Count}");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.GetArrayLength() != 100)
                throw new InvalidOperationException("Persisted JSON row count mismatch");
            if (Directory.EnumerateFiles(directory, $"{fileName}.*.tmp").Any())
                throw new InvalidOperationException("Temporary JSON file was not cleaned up");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            foreach (var temp in Directory.EnumerateFiles(directory, $"{fileName}.*.tmp"))
                File.Delete(temp);
        }
    }
}
