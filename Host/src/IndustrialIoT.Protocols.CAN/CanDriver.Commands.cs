namespace IndustrialIoT.Protocols.CAN;

using System.Globalization;
using System.Threading.Channels;
using IndustrialIoT.Domain.Enums;
using Microsoft.Extensions.Logging;

public sealed partial class CanDriver
{
    private int _awaitingReply;

    private async Task<string> CommandAsync(string command, CancellationToken ct, bool allowRejection = false, bool requireConnected = false)
    {
        await _commands.WaitAsync(ct);
        var sent = false;
        try
        {
            if (requireConnected) EnsureConnected();
            var transport = _transport ?? throw new IOException("SLCAN serial transport is closed.");
            var responses = _responses ?? throw new IOException("SLCAN response channel is closed.");
            var session = _session ?? throw new IOException("SLCAN session is closed.");
            if (session.IsCancellationRequested) throw new IOException("SLCAN session has stopped; reconnect before sending commands.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
            timeout.CancelAfter(_readTimeout);
            // Once a command may have reached the adapter, cancellation makes its late reply ambiguous.
            Volatile.Write(ref _awaitingReply, 1);
            sent = true;
            await transport.WriteLineAsync(command, timeout.Token);
            var reply = await responses.Reader.ReadAsync(timeout.Token);
            if (reply == "\a")
            {
                if (allowRejection) return reply;
                throw new SlcanRejectedException($"SLCAN adapter rejected command {command[0]}.");
            }
            var expected = command[0] switch { 't' or 'r' => "z", 'T' or 'R' => "Z", _ => "" };
            if (command == "F")
            {
                if (reply.Length == 3 && reply[0] == 'F'
                    && byte.TryParse(reply.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)) return reply;
            }
            else if (reply == expected || (expected.Length > 0 && reply.Length == 0)) return reply;
            throw new IOException($"Unexpected SLCAN response to command {command[0]}.");
        }
        catch (SlcanRejectedException) { throw; }
        catch (OperationCanceledException) when (sent)
        {
            Fault("SLCAN command interrupted or timed out; reconnect to resynchronize replies.");
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("SLCAN command timed out or the session stopped.");
        }
        catch (Exception ex) when (sent && ex is IOException or ChannelClosedException)
        {
            Fault(ex.Message);
            throw;
        }
        finally
        {
            Volatile.Write(ref _awaitingReply, 0);
            _commands.Release();
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_state != ConnectionState.Connected) return false;
        try
        {
            var response = await CommandAsync("F", ct, requireConnected: true);
            var healthy = response == "F00";
            if (!healthy) _logger.LogWarning("SLCAN adapter status {Status}", response);
            return healthy;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected) throw new IOException("CAN driver is not connected.");
    }

    private void Fault(string message)
    {
        SetState(ConnectionState.Faulted, message);
        _session?.Cancel();
        _responses?.Writer.TryComplete(new IOException(message));
        ClearFrames();
        _logger.LogWarning("SLCAN connection fault: {Reason}", message);
    }

    private sealed class SlcanRejectedException(string message) : IOException(message);
}
