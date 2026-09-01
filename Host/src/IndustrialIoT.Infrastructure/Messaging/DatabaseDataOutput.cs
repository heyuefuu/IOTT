namespace IndustrialIoT.Infrastructure.Messaging;

using System.Text.Json;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Infrastructure.Persistence;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public sealed class DatabaseOutputOptions
{
    public const string SectionName = "DataOutput:Database";
    public bool Enabled { get; set; }
}

public sealed class DatabaseDataOutput : IDataOutput
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseDataOutput> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DatabaseDataOutput(IServiceScopeFactory scopeFactory, ILogger<DatabaseDataOutput> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string Name => "Database";
    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task WriteAsync(CollectedDataBatch batch, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IoTDbContext>();
        db.RealtimeDataRecords.Add(new RealtimeDataRecord
        {
            DeviceId = batch.DeviceId,
            GroupName = batch.GroupName,
            PayloadJson = JsonSerializer.Serialize(batch, _jsonOptions),
            CollectedAt = batch.CollectedAt,
            StoredAt = DateTimeOffset.UtcNow,
            ValueCount = batch.Values.Count,
            CollectionDurationMs = batch.CollectionDuration.TotalMilliseconds,
        });
        await db.SaveChangesAsync(ct);
        _logger.LogDebug("Stored realtime batch for {DeviceId}/{Group}", batch.DeviceId, batch.GroupName);
    }

    public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
}
