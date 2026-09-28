namespace IndustrialIoT.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

public static class DatabaseBootstrapper
{
    public static async Task InitializeAsync(IoTDbContext db, CancellationToken ct = default)
    {
        // Creates the schema in a new MySQL database. Legacy T-SQL scripts are archival only.
        await db.Database.EnsureCreatedAsync(ct);
    }
}
