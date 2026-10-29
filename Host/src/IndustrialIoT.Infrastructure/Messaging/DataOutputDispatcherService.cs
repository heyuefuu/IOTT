namespace IndustrialIoT.Infrastructure.Messaging;

using System.Collections.Concurrent;
using System.Diagnostics;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Pipeline;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public partial class DataOutputDispatcherService : BackgroundService
{
    private readonly ICollectionPipeline _pipeline;
    private readonly IEnumerable<IDataOutput> _outputs;
    private readonly ILogger<DataOutputDispatcherService> _logger;
    private readonly ConcurrentDictionary<string, long> _errorCounts = new();
    private long _totalDispatched;

    public DataOutputDispatcherService(
        ICollectionPipeline pipeline,
        IEnumerable<IDataOutput> outputs,
        ILogger<DataOutputDispatcherService> logger)
    {
        _pipeline = pipeline;
        _outputs = outputs.ToArray();
        _logger = logger;
    }

    public long TotalDispatched => Interlocked.Read(ref _totalDispatched);

    public IReadOnlyDictionary<string, long> GetErrorCounts() =>
        _errorCounts.ToDictionary(kv => kv.Key, kv => kv.Value);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DataOutputDispatcher starting — initializing outputs");

        // Initialize all outputs
        foreach (var output in _outputs)
        {
            if (output is LocalHistoryDataOutput)
            {
                await RetryHistoryAsync(output, null, stoppingToken);
                _errorCounts.TryAdd(output.Name, 0);
                continue;
            }
            try
            {
                await output.InitializeAsync(stoppingToken);
                _errorCounts[output.Name] = 0;
                _logger.LogInformation("Output {Name} initialized", output.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize output {Name} — it will be skipped", output.Name);
            }
        }

        var reader = _pipeline.GetOutputReader();
        _logger.LogInformation("DataOutputDispatcher reading from pipeline channel");

        var durableOutputs = _outputs.OfType<LocalHistoryDataOutput>().Where(output => output.Policy.Enabled).ToArray();
        while (await reader.WaitToReadAsync(stoppingToken))
        {
            if (!reader.TryRead(out var first)) continue;
            var batches = durableOutputs.Length == 0 ? new[] { first }
                : await ReadMicroBatchAsync(reader, first, stoppingToken);
            foreach (var history in durableOutputs)
                await RetryHistoryGroupAsync(history, batches, stoppingToken);
            foreach (var batch in batches) await DispatchBatchAsync(batch, stoppingToken);
        }

        // Graceful shutdown: flush all outputs
        _logger.LogInformation("DataOutputDispatcher shutting down — flushing outputs");
        foreach (var output in _outputs)
        {
            try
            {
                await output.FlushAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error flushing output {Name} during shutdown", output.Name);
            }
        }

        _logger.LogInformation("DataOutputDispatcher stopped. Total dispatched: {Total}, errors: {Errors}",
            _totalDispatched, string.Join(", ", _errorCounts.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    private async Task DispatchBatchAsync(CollectedDataBatch batch, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var tasks = _outputs.Where(output => output is not LocalHistoryDataOutput)
            .Select(output => DispatchToOutputAsync(output, batch, ct));
        await Task.WhenAll(tasks);
        sw.Stop();

        Interlocked.Increment(ref _totalDispatched);

        if (sw.ElapsedMilliseconds > 500)
        {
            _logger.LogWarning("Slow dispatch for device {DeviceId}: {ElapsedMs}ms",
                batch.DeviceId, sw.ElapsedMilliseconds);
        }
    }

    private async Task DispatchToOutputAsync(IDataOutput output, CollectedDataBatch batch, CancellationToken ct)
    {
        try
        {
            await output.WriteAsync(batch, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _errorCounts.AddOrUpdate(output.Name, 1, (_, count) => count + 1);
            _logger.LogError(ex, "Output {Name} failed for device {DeviceId} group {Group}",
                output.Name, batch.DeviceId, batch.GroupName);
        }
    }

    private async Task RetryHistoryAsync(IDataOutput output, CollectedDataBatch? batch, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (batch is null) await output.InitializeAsync(ct);
                else await output.WriteAsync(batch, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                _errorCounts.AddOrUpdate(output.Name, 1, (_, count) => count + 1);
                _logger.LogError(error, "Durable local history failed; dispatch paused and will retry without dropping the current batch");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }
}
