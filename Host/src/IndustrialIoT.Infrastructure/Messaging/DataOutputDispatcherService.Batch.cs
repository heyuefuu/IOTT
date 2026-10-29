namespace IndustrialIoT.Infrastructure.Messaging;

using System.Threading.Channels;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging;

public partial class DataOutputDispatcherService
{
    private static async Task<CollectedDataBatch[]> ReadMicroBatchAsync(ChannelReader<CollectedDataBatch> reader,
        CollectedDataBatch first, CancellationToken ct)
    {
        var batches = new List<CollectedDataBatch> { first };
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromMilliseconds(50));
        while (batches.Count < 100)
        {
            while (batches.Count < 100 && reader.TryRead(out var next)) batches.Add(next);
            if (batches.Count == 100) break;
            try { if (!await reader.WaitToReadAsync(window.Token)) break; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
        }
        return batches.ToArray();
    }

    private async Task RetryHistoryGroupAsync(LocalHistoryDataOutput history, IReadOnlyList<CollectedDataBatch> batches, CancellationToken ct)
    {
        var committed = 0;
        while (committed < batches.Count)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await history.WriteManyAsync(batches.Skip(committed).ToArray(), ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (LocalHistoryWriteException error)
            {
                if (error.CommittedBatches < 0 || error.CommittedBatches > batches.Count - committed) throw;
                committed += error.CommittedBatches;
                _errorCounts.AddOrUpdate(history.Name, 1, (_, count) => count + 1);
                _logger.LogError(error, "Durable history group failed after {Committed} batches; remaining data is retained for retry", committed);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }
}
