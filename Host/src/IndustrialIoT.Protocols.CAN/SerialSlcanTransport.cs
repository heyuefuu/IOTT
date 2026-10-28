namespace IndustrialIoT.Protocols.CAN;

using System.IO.Ports;
using System.Text;
using IndustrialIoT.Domain.ValueObjects;

internal sealed class SerialSlcanTransport : ISlcanTransport
{
    private SerialPort? _port;
    private readonly StringBuilder _line = new();

    public async Task OpenAsync(DeviceConnectionConfig config, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var options = CanOptions.Parse(config);
        var port = new SerialPort(options.PortName, options.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 100,
            WriteTimeout = Math.Max(1, (int)config.ReadTimeout.TotalMilliseconds),
            Encoding = Encoding.ASCII,
        };
        var opening = Task.Run(port.Open);
        try
        {
            await opening.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            port.DiscardInBuffer();
            _line.Clear();
            _port = port;
        }
        catch
        {
            if (opening.IsCompleted) port.Dispose();
            else _ = DisposeAfterOperationAsync(opening, port);
            throw;
        }
    }

    private static async Task DisposeAfterOperationAsync(Task operation, SerialPort port)
    {
        try { await operation; }
        catch { }
        finally { port.Dispose(); }
    }

    public async Task WriteLineAsync(string command, CancellationToken ct)
    {
        var port = _port ?? throw new IOException("SLCAN serial port is closed.");
        var writing = Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            port.Write(command + "\r");
            ct.ThrowIfCancellationRequested();
        }, ct);
        try { await writing.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { await DisposeAsync(); }
            finally { _ = DisposeAfterOperationAsync(writing, port); }
            throw;
        }
    }

    public Task<string> ReadLineAsync(CancellationToken ct) => Task.Run(() =>
    {
        var port = _port ?? throw new IOException("SLCAN serial port is closed.");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int received;
            try { received = port.ReadByte(); }
            catch (TimeoutException) { continue; }
            if (received < 0) throw new EndOfStreamException("SLCAN serial port closed during reception.");
            if (received == 7) { _line.Clear(); return "\a"; }
            if (received == 13)
            {
                var result = _line.ToString();
                _line.Clear();
                return result;
            }
            if (received == 10) continue;
            if (received is < 32 or > 126 || _line.Length >= 64)
                throw new IOException("Invalid or oversized SLCAN serial response.");
            _line.Append((char)received);
        }
    }, ct);

    public ValueTask DisposeAsync()
    {
        var port = Interlocked.Exchange(ref _port, null);
        port?.Dispose();
        return ValueTask.CompletedTask;
    }
}
