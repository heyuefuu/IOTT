namespace IndustrialIoT.Infrastructure.Messaging;

using IndustrialIoT.Protocols.Models;

public interface IDataOutput
{
    string Name { get; }
    Task InitializeAsync(CancellationToken ct);
    Task WriteAsync(CollectedDataBatch batch, CancellationToken ct);
    Task FlushAsync(CancellationToken ct);
}
