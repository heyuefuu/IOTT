namespace MachineConnectionApi.Tests;

using System.Reflection;
using MachineConnectionApi.Controllers;

internal static class TelemetryInfluxSqlRegressionTests
{
    public static void HistoryPagingUsesStableOrdering()
    {
        var method = typeof(TelemetryInfluxController).GetMethod(
            "BuildHistoryDataSql", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("History SQL builder not found");
        var sql = (string?)method.Invoke(null,
            ["datapoint", "device_id = 'device-1'", 2, 20]) ?? "";
        const string order =
            "ORDER BY time DESC, path ASC, point_name ASC, data_type ASC, status ASC";
        if (!sql.Contains(order, StringComparison.Ordinal))
            throw new InvalidOperationException("Telemetry history paging order is unstable");
        if (!sql.Contains("LIMIT 20", StringComparison.Ordinal) ||
            !sql.Contains("OFFSET 20", StringComparison.Ordinal))
            throw new InvalidOperationException("Telemetry history page window is incorrect");
    }
}
