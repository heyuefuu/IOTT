namespace IndustrialIoT.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

public static class DatabaseBootstrapper
{
    public static async Task InitializeAsync(IoTDbContext db, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);

        if (!db.Database.IsSqlServer())
            return;

        foreach (var scriptPath in GetSqlScriptPaths())
        {
            var sql = await File.ReadAllTextAsync(scriptPath, ct);
            if (!string.IsNullOrWhiteSpace(sql))
                await db.Database.ExecuteSqlRawAsync(sql, ct);
        }
    }

    internal static IReadOnlyList<string> GetSqlScriptPaths(string? baseDirectory = null)
    {
        var scriptsDirectory = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "Scripts");
        if (!Directory.Exists(scriptsDirectory))
            return [];

        return Directory.GetFiles(scriptsDirectory, "*.sql")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
