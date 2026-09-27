using System.Collections;
using System.Linq.Expressions;
using System.Net;
using System.Text.Json;
using MachineConnectionApi.Controllers;
using MachineConnectionApi.Data;
using MachineConnectionApi.Entities;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace MachineConnectionApi.Tests;

internal static partial class CncGatewayRoutingRegressionTests
{
    private sealed class ImportDeviceStore(params MachineDeviceDto[] devices) : IDeviceStore
    {
        private List<MachineDeviceDto> _devices = [.. devices];
        public List<MachineDeviceDto> ReadAll() => [.. _devices];
        public void WriteAll(IEnumerable<MachineDeviceDto> items) => _devices = [.. items];
        public TResult Update<TResult>(Func<List<MachineDeviceDto>, TResult> update) => update(_devices);
    }

    private static async Task RobotImportPreservesRegistry()
    {
        const string addressMap = "[{\"Path\":\"/Robot/Ready\",\"ModbusAddress\":\"240\",\"DataType\":\"UInt16\"}]";
        foreach (var protocol in new[] { "HuazhongRobot", "ModbusTCP" })
        foreach (var successCount in new[] { 0, 1 })
        {
            var device = new MachineDeviceDto { Id = "robot-import", Name = "Robot", Type = "Robot",
                Brand = "HSR", Model = "HSR", Protocol = protocol, Status = "Offline",
                Host = "127.0.0.1", Port = 502, CreatedAt = DateTimeOffset.UtcNow,
                ExtendedProperties = new() { ["Station"] = "7", ["AddressMap"] = "[]" } };
            var other = device with { Id = "other" };
            var store = new ImportDeviceStore(device, other);
            var response = JsonSerializer.Serialize(new { successCount, addressMap });
            using var host = new HostFixture { ResponseText = response };
            var controller = WithRequest(new CollectionController(host, Configuration,
                NullLogger<CollectionController>.Instance, store));
            controller.Request.ContentType = "multipart/form-data; boundary=robot-fixture";
            var bytes = System.Text.Encoding.UTF8.GetBytes("Address,DataType\n240,UInt16");
            controller.Request.Form = new Microsoft.AspNetCore.Http.FormCollection(
                new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(),
                new Microsoft.AspNetCore.Http.FormFileCollection { new Microsoft.AspNetCore.Http.FormFile(
                    new MemoryStream(bytes), 0, bytes.Length, "file", "robot.csv")
                    { Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(), ContentType = "text/csv" } });
            var result = await controller.ImportTags(device.Id, CancellationToken.None);
            AssertContent(result, 200, response);
            var request = AssertRequest(host, HttpMethod.Post, "/api/batch-import/tags/robot-import");
            if (!request.Parts.Single().Body.SequenceEqual(bytes))
                throw new InvalidOperationException("Robot import corrupted the uploaded file.");
            var saved = store.ReadAll().Single(row => row.Id == device.Id);
            var expected = protocol == "HuazhongRobot" && successCount > 0 ? addressMap : "[]";
            if (saved.ExtendedProperties["AddressMap"] != expected || saved.ExtendedProperties["Station"] != "7"
                || store.ReadAll().Single(row => row.Id == "other").ExtendedProperties["AddressMap"] != "[]")
                throw new InvalidOperationException("Robot import failed to retain and isolate gateway device mappings.");
        }
    }

    private static async Task RobotImportsSerializeResponses()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var device = new MachineDeviceDto { Id = "robot-concurrent", Name = "Robot", Type = "Robot",
            Brand = "HSR", Model = "HSR", Protocol = "HuazhongRobot", Status = "Offline",
            Host = "127.0.0.1", Port = 502, CreatedAt = DateTimeOffset.UtcNow,
            ExtendedProperties = new() { ["AddressMap"] = "[]" } };
        var store = new ImportDeviceStore(device);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new HostFixture();
        host.BeforeResponse = async count =>
        {
            if (count == 1) { started.TrySetResult(); await release.Task.WaitAsync(timeout.Token); }
            host.ResponseText = JsonSerializer.Serialize(new { successCount = 1,
                addressMap = count == 1 ? "[\"First\"]" : "[\"First\",\"Second\"]" });
        };
        CollectionController Controller() => WithRequest(new CollectionController(host, Configuration,
            NullLogger<CollectionController>.Instance, store));
        var first = Controller().ImportTags(device.Id, timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        var second = Controller().ImportTags(device.Id, timeout.Token);
        try
        {
            if (host.Requests.Count != 1 || store.ReadAll()[0].ExtendedProperties["AddressMap"] != "[]")
                throw new InvalidOperationException("Gateway imports forwarded concurrently before saving the first response.");
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(first, second);
        if (host.Requests.Count != 2 || store.ReadAll()[0].ExtendedProperties["AddressMap"] != "[\"First\",\"Second\"]")
            throw new InvalidOperationException("Gateway import responses lost the newest map.");
        host.BeforeResponse = null;
        host.ThrowOnSend = true;
        await Controller().ImportTags(device.Id, timeout.Token);
        host.ThrowOnSend = false;
        AssertContent(await Controller().ImportTags(device.Id, timeout.Token), 200,
            System.Text.Encoding.UTF8.GetString(host.ResponseBytes));
    }

    private static async Task LegacyCollectionUsesHost()
    {
        foreach (var device in Devices)
        foreach (var state in new[] { "good", "missing", "bad", "offline" })
        {
            Datacollection[] rows =
            [
                new() { DeviceId = device.Id, Name = "legacy", Path = device.Address, Datatype = "Int32", Protocol = "NCLinkApi" },
                new() { DeviceId = device.Id, Name = "current", Path = device.Address + "-second", Datatype = "Int32", Protocol = "IndustrialIoT" },
                new() { DeviceId = "another-device", Name = "unrelated", Path = "other", Protocol = "NCLinkApi" },
            ];
            var tags = rows.Take(state == "missing" ? 1 : 2).Select((row, index) => new
            {
                address = row.Path, value = index, quality = state == "bad" && index == 1 ? "Bad" : "Good",
                timestamp = "2026-09-23T00:00:00Z",
            });
            using var host = new HostFixture { ResponseText = JsonSerializer.Serialize(new { tags }) };
            if (state == "offline") host.Status = HttpStatusCode.ServiceUnavailable;
            using var database = new MemoryCollectionContext(rows);
            var telemetry = new RecordingTelemetry();
            var controller = WithRequest(new DatacollectionController(database, host, Configuration, telemetry, telemetry,
                NullLogger<DatacollectionController>.Instance));
            var result = await controller.Collect(device.Id, CancellationToken.None);
            var request = AssertRequest(host, HttpMethod.Post, $"/api/data/{device.Id}/read");
            using var payload = JsonDocument.Parse(request.Body);
            if (payload.RootElement.GetProperty("tags").GetArrayLength() != 2)
                throw new InvalidOperationException("Collection read must retain both legacy/current points and isolate device IDs.");
            if (state == "offline")
            {
                if (result is not ObjectResult { StatusCode: 503 } || telemetry.Written != 0 || telemetry.Published != 0)
                    throw new InvalidOperationException("Collection upstream failure must survive without fallback telemetry.");
                continue;
            }
            if (result is not OkObjectResult { Value: DatacollectionCollectResultDto collected } ||
                collected.TotalCount != 2 || collected.SuccessCount != (state == "good" ? 2 : 1) ||
                telemetry.Written != 1 || telemetry.Published != 1)
                throw new InvalidOperationException("Mixed legacy/current points must collect through Host with accurate success counts.");
            if (state != "good" && collected.Items.Count(item => !string.IsNullOrWhiteSpace(item.ErrorMessage)) != 1)
                throw new InvalidOperationException("Missing or Bad-quality values must report one failed point.");
        }
    }

    private sealed class RecordingTelemetry : IInfluxTelemetryWriter, IMqttTelemetryPublisher
    {
        public int Written { get; private set; }
        public int Published { get; private set; }
        public Task WriteBatchAsync(string deviceId, DateTimeOffset batchTime, IReadOnlyList<InfluxTelemetryPoint> points, CancellationToken ct = default)
        {
            Written++;
            return Task.CompletedTask;
        }
        public Task PublishCollectionBatchAsync(string deviceId, DateTimeOffset collectedAt, IReadOnlyList<InfluxTelemetryPoint> points, CancellationToken ct = default)
        {
            Published++;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryCollectionContext(IEnumerable<Datacollection> rows)
        : MCConfigurationDbContext(new DbContextOptionsBuilder<MCConfigurationDbContext>().Options)
    {
        private readonly DbSet<Datacollection> points = new MemorySet<Datacollection>(rows);
        public override DbSet<TEntity> Set<TEntity>() => typeof(TEntity) == typeof(Datacollection)
            ? (DbSet<TEntity>)(object)points : base.Set<TEntity>();
    }

    private sealed class MemorySet<TEntity>(IEnumerable<TEntity> rows) : DbSet<TEntity>, IQueryable<TEntity> where TEntity : class
    {
        private readonly IQueryable<TEntity> query = new MemoryQuery<TEntity>(rows);
        public override IEntityType EntityType => throw new NotSupportedException("Read-only query fixture");
        Type IQueryable.ElementType => query.ElementType;
        Expression IQueryable.Expression => query.Expression;
        IQueryProvider IQueryable.Provider => query.Provider;
        IEnumerator<TEntity> IEnumerable<TEntity>.GetEnumerator() => query.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => query.GetEnumerator();
    }

    private sealed class MemoryQuery<TEntity> : EnumerableQuery<TEntity>, IAsyncEnumerable<TEntity>, IQueryable<TEntity>
    {
        public MemoryQuery(IEnumerable<TEntity> items) : base(items) { }
        public MemoryQuery(Expression expression) : base(expression) { }
        IQueryProvider IQueryable.Provider => new MemoryQueryProvider(this);
        public IAsyncEnumerator<TEntity> GetAsyncEnumerator(CancellationToken ct = default) => new MemoryEnumerator<TEntity>(this.AsEnumerable().GetEnumerator());
    }

    private sealed class MemoryQueryProvider(IQueryProvider inner) : IQueryProvider
    {
        public IQueryable CreateQuery(Expression expression) => inner.CreateQuery(expression);
        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new MemoryQuery<TElement>(expression);
        public object? Execute(Expression expression) => inner.Execute(expression);
        public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(expression);
    }

    private sealed class MemoryEnumerator<TEntity>(IEnumerator<TEntity> inner) : IAsyncEnumerator<TEntity>
    {
        public TEntity Current => inner.Current;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(inner.MoveNext());
        public ValueTask DisposeAsync()
        {
            inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
