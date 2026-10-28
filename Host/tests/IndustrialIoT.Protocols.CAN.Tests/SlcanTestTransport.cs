namespace IndustrialIoT.Protocols.CAN.Tests;

using System.Collections.Concurrent;
using System.Threading.Channels;
using IndustrialIoT.Domain.ValueObjects;

internal sealed class SlcanTestTransport : ISlcanTransport
{
    private Channel<string> _incoming = Channel.CreateUnbounded<string>();
    public ConcurrentQueue<string> Commands { get; } = new();
    public DeviceConnectionConfig? OpenedConfiguration { get; private set; }
    public bool Disposed { get; private set; }
    public int OpenCount { get; private set; }
    public TaskCompletionSource DisposeObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Func<string, string?>? Reply { get; set; }
    public Func<string, CancellationToken, Task>? BeforeReply { get; set; }

    public Task OpenAsync(DeviceConnectionConfig config, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Disposed) _incoming = Channel.CreateUnbounded<string>();
        Disposed = false;
        OpenCount++;
        OpenedConfiguration = config;
        return Task.CompletedTask;
    }

    public async Task WriteLineAsync(string command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Disposed) throw new ObjectDisposedException(nameof(SlcanTestTransport));
        if (command.Contains('\r') || command.Contains('\n'))
            throw new InvalidOperationException("Transport commands must not contain line terminators");
        Commands.Enqueue(command);
        if (BeforeReply is not null) await BeforeReply(command, ct);
        var response = Reply is null ? DefaultReply(command) : Reply(command);
        if (response is not null) Inject(response);
    }

    public async Task<string> ReadLineAsync(CancellationToken ct) =>
        await _incoming.Reader.ReadAsync(ct);

    public void Inject(string line)
    {
        if (!_incoming.Writer.TryWrite(line))
            throw new InvalidOperationException("Cannot inject into closed test transport");
    }

    public void Fail() => _incoming.Writer.TryComplete(new IOException("SLCAN serial disconnected"));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _incoming.Writer.TryComplete();
        DisposeObserved.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public static string DefaultReply(string command) => command switch
    {
        "F" => "F00",
        _ when command.StartsWith('t') => "z",
        _ when command.StartsWith('T') => "Z",
        _ => ""
    };
}
