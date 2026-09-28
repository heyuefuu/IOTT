using IndustrialIoT.Infrastructure.Persistence;
using MachineConnectionApi.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using System.Text.Json;

var mode = args.FirstOrDefault() ?? "--inspect";
if (mode is not ("--inspect" or "--apply" or "--verify"))
    throw new ArgumentException("Use --inspect, --apply, or --verify; optional second argument is a JSON report path.");
string Required(string key) => Environment.GetEnvironmentVariable(key)
    ?? throw new InvalidOperationException($"Set {key} before running the migration.");
var sourceHost = Required("SQLSERVER_HOST_CONNECTION");
var sourceGateway = Required("SQLSERVER_GATEWAY_CONNECTION");
var targetHost = Required("MYSQL_HOST_CONNECTION");
var targetGateway = Required("MYSQL_GATEWAY_CONNECTION");
var hostBuilder = new MySqlConnectionStringBuilder(targetHost);
var gatewayBuilder = new MySqlConnectionStringBuilder(targetGateway);
if (hostBuilder.Server != gatewayBuilder.Server || hostBuilder.Port != gatewayBuilder.Port
    || hostBuilder.UserID != gatewayBuilder.UserID || hostBuilder.Database == gatewayBuilder.Database)
    throw new InvalidOperationException("Targets must be distinct databases on the same MySQL instance and account.");
var tables = new[]
{
    new TableCopy(sourceHost, hostBuilder.Database, "Devices"),
    new TableCopy(sourceHost, hostBuilder.Database, "CollectionProfiles"),
    new TableCopy(sourceHost, hostBuilder.Database, "CollectionGroups"),
    new TableCopy(sourceHost, hostBuilder.Database, "TagConfigs"),
    new TableCopy(sourceHost, hostBuilder.Database, "NCPrograms"),
    new TableCopy(sourceHost, hostBuilder.Database, "RealtimeDataRecords"),
    new TableCopy(sourceGateway, gatewayBuilder.Database, "datacollection")
};
var reports = new List<TableReport>();
if (mode == "--inspect")
{
    foreach (var table in tables)
        reports.Add(await table.InspectAsync());
}
else
{
    if (mode == "--apply")
    {
        var version = new MySqlServerVersion(new Version(8, 4, 0));
        await using var host = new IoTDbContext(new DbContextOptionsBuilder<IoTDbContext>()
            .UseMySql(targetHost, version).Options);
        await using var gateway = new MCConfigurationDbContext(new DbContextOptionsBuilder<MCConfigurationDbContext>()
            .UseMySql(targetGateway, version).Options);
        await host.Database.EnsureCreatedAsync();
        await gateway.Database.EnsureCreatedAsync();
    }
    await using var target = new MySqlConnection(targetHost);
    await target.OpenAsync();
    await using var transaction = await target.BeginTransactionAsync();
    if (mode == "--apply")
    {
        foreach (var table in tables)
            await table.RequireEmptyAsync(target, transaction);
        foreach (var table in tables)
            reports.Add(await table.CopyAsync(target, transaction));
        await transaction.CommitAsync();
    }
    else
    {
        foreach (var table in tables)
            reports.Add(await table.VerifyAsync(target, transaction));
    }
}
var output = JsonSerializer.Serialize(new { mode, utc = DateTimeOffset.UtcNow, tables = reports },
    new JsonSerializerOptions { WriteIndented = true });
if (args.Length > 1) await File.WriteAllTextAsync(args[1], output);
Console.WriteLine(output);

internal record TableReport(string Database, string Table, long Rows, string Sha256, bool Verified);
