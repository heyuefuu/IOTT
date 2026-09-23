using System.Data.Common;
using System.Text.Json;
using MachineConnectionApi.Controllers;
using MachineConnectionApi.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace MachineConnectionApi.Tests;

internal static class DatacollectionSaveRegressionTests
{
    private const string FailureMessage = "Simulated database pre-login handshake failure.";

    public static async Task RunAll()
    {
        var interceptor = new FailingConnectionInterceptor();
        var options = new DbContextOptionsBuilder<MCConfigurationDbContext>()
            .UseSqlServer("Server=unused.invalid;Database=point-save-fixture;Integrated Security=True;")
            .AddInterceptors(interceptor)
            .Options;
        await using var database = new SaveTrackingContext(options);
        var controller = new DatacollectionController(database, null!, null!, null!, null!,
            NullLogger<DatacollectionController>.Instance);
        var request = new DatacollectionSyncRequest("fixture-device",
            [new DatacollectionSyncItem("State", "i=2259", "Int32", 1000)], ["i=2259"]);

        var result = await controller.Sync(request, CancellationToken.None);

        if (result is not ObjectResult { StatusCode: 500, Value: not null } failure)
            throw new InvalidOperationException("Connection failure must return a structured HTTP 500 response.");
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(failure.Value));
        if (!response.RootElement.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(error.GetString()) ||
            response.RootElement.TryGetProperty("saved", out _))
            throw new InvalidOperationException("Connection failure must contain an error and must not report saved points.");
        if (interceptor.Attempts != 1 || database.SaveCalls != 0)
            throw new InvalidOperationException(
                $"Expected one intercepted connection attempt and no writes; got {interceptor.Attempts} attempts and {database.SaveCalls} writes.");
    }

    private sealed class FailingConnectionInterceptor : DbConnectionInterceptor
    {
        public int Attempts { get; private set; }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException(FailureMessage);
        }
    }

    private sealed class SaveTrackingContext(DbContextOptions<MCConfigurationDbContext> options)
        : MCConfigurationDbContext(options)
    {
        public int SaveCalls { get; private set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            throw new InvalidOperationException("SaveChanges must not run after the connection fails.");
        }
    }
}
