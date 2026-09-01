namespace IndustrialIoT.Protocols.Abstractions;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Models;

public interface IProtocolDriver : IAsyncDisposable
{
    ProtocolType Protocol { get; }
    ConnectionState State { get; }
    DriverCapabilities Capabilities { get; }
    event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task<bool> PingAsync(CancellationToken ct = default);

    Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default);
    Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default);
    Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default);
}

[Flags]
public enum DriverCapabilities
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    Browse = 1 << 2,
    Subscribe = 1 << 3,
    FileTransfer = 1 << 4,
    BatchRead = 1 << 5,
    BatchWrite = 1 << 6,
}

public class ConnectionStateChangedEventArgs : EventArgs
{
    public required ConnectionState OldState { get; init; }
    public required ConnectionState NewState { get; init; }
    public string? Reason { get; init; }
}
