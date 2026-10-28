namespace IndustrialIoT.Protocols.CAN;

using System.Diagnostics;
using System.Globalization;
using IndustrialIoT.Domain.Enums;
using Microsoft.Extensions.Logging;

public sealed partial class CanDriver
{
    private sealed record ReceivedFrame(byte[] Payload, DateTimeOffset Timestamp, long ReceivedAt);
    private readonly object _framesGate = new();
    private readonly Dictionary<(bool Extended, uint Id), ReceivedFrame> _frames = [];
    private readonly Queue<(bool Extended, uint Id)> _frameOrder = new();
    private TaskCompletionSource _changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var line = await _transport!.ReadLineAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (line.Length > 0 && line[0] is 't' or 'T' or 'r' or 'R')
                {
                    if (!AcceptFrame(line)) _logger.LogDebug("Ignored remote or malformed SLCAN frame");
                }
                else
                {
                    if (Interlocked.Exchange(ref _awaitingReply, 0) == 0)
                        throw new IOException("Unsolicited SLCAN command response; reconnect to resynchronize replies.");
                    if (!_responses!.Writer.TryWrite(line))
                        throw new IOException("SLCAN adapter response queue overflowed.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Fault(ex.Message);
        }
    }

    private bool AcceptFrame(string line)
    {
        var extended = line[0] is 'T' or 'R';
        var idLength = extended ? 8 : 3;
        var prefixLength = idLength + 2;
        if (line.Length < prefixLength || !uint.TryParse(line.AsSpan(1, idLength), NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture, out var id) || id > (extended ? 0x1FFFFFFFU : 0x7FFU)) return false;
        var length = line[idLength + 1] - '0';
        if (length is < 0 or > 8 || line[0] is 'r' or 'R') return false;
        var expected = prefixLength + length * 2;
        if (line.Length != expected && line.Length != expected + 4) return false;
        if (line.Length == expected + 4 && !ushort.TryParse(line.AsSpan(expected), NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture, out _)) return false;
        byte[] bytes;
        try { bytes = Convert.FromHexString(line.AsSpan(prefixLength, length * 2)); }
        catch (FormatException) { return false; }
        lock (_framesGate)
        {
            if (!_frames.ContainsKey((extended, id)))
            {
                if (_frames.Count >= 4096) _frames.Remove(_frameOrder.Dequeue());
                _frameOrder.Enqueue((extended, id));
            }
            _frames[(extended, id)] = new(bytes, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
            PulseReaders();
        }
        return true;
    }

    private void ClearFrames()
    {
        lock (_framesGate)
        {
            _frames.Clear();
            _frameOrder.Clear();
            PulseReaders();
        }
    }

    private void PulseReaders()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private async Task<ReceivedFrame> ReadFrameAsync(CanAddress address, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_readTimeout);
        while (true)
        {
            Task changed;
            lock (_framesGate)
            {
                EnsureConnected();
                if (_frames.TryGetValue(address.Key, out var frame)
                    && Stopwatch.GetElapsedTime(frame.ReceivedAt) <= _options!.MaxAge) return frame;
                changed = _changed.Task;
            }
            try { await changed.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("No fresh CAN data frame was received for this ID before the read timeout."); }
        }
    }
}
