namespace IndustrialIoT.Protocols.CAN;

using IndustrialIoT.Domain.ValueObjects;

public interface ISlcanTransport : IAsyncDisposable
{
    Task OpenAsync(DeviceConnectionConfig config, CancellationToken ct);
    Task WriteLineAsync(string command, CancellationToken ct);
    Task<string> ReadLineAsync(CancellationToken ct);
}
