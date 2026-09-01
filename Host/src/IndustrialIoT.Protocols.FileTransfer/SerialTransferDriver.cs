namespace IndustrialIoT.Protocols.FileTransfer;

using System.Diagnostics;
using System.IO.Ports;
using System.Security.Cryptography;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;

/// <summary>
/// Serial file transfer driver using a simple chunked protocol.
/// Frame format: [STX(1)] [SeqNo(2)] [Length(2)] [Data(N)] [Checksum(2)] [ETX(1)]
/// STX = 0x02, ETX = 0x03. Length and SeqNo are big-endian uint16.
/// Checksum = CRC-16/CCITT over SeqNo+Length+Data bytes.
/// Flow control: software XON/XOFF. ACK = 0x06, NAK = 0x15.
/// </summary>
[ProtocolDriver(ProtocolType.Serial, "Serial", "*")]
public class SerialTransferDriver : IProtocolDriver, INCProgramTransfer
{
    private readonly ILogger<SerialTransferDriver> _logger;
    private SerialPort? _serialPort;
    private ConnectionState _state = ConnectionState.Disconnected;
    private DeviceConnectionConfig? _config;

    private const byte STX = 0x02;
    private const byte ETX = 0x03;
    private const byte ACK = 0x06;
    private const byte NAK = 0x15;
    private const int MaxChunkSize = 1024;
    private const int MaxRetries = 3;
    private const int AckTimeoutMs = 5000;

    public ProtocolType Protocol => ProtocolType.Serial;
    public ConnectionState State => _state;
    public DriverCapabilities Capabilities => DriverCapabilities.FileTransfer;
    public bool SupportsResume => false;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public SerialTransferDriver(ILogger<SerialTransferDriver> logger)
    {
        _logger = logger;
    }

    public Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        TransitionState(ConnectionState.Connecting);
        _config = config;

        try
        {
            var portName = config.ExtendedProperties.GetValueOrDefault("PortName", "COM3");
            var baudRate = int.Parse(config.ExtendedProperties.GetValueOrDefault("BaudRate", "9600"));
            var parity = Enum.Parse<Parity>(config.ExtendedProperties.GetValueOrDefault("Parity", "None"), ignoreCase: true);
            var dataBits = int.Parse(config.ExtendedProperties.GetValueOrDefault("DataBits", "8"));
            var stopBits = Enum.Parse<StopBits>(config.ExtendedProperties.GetValueOrDefault("StopBits", "One"), ignoreCase: true);

            _serialPort = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
            {
                ReadTimeout = (int)config.ReadTimeout.TotalMilliseconds,
                WriteTimeout = (int)config.ConnectTimeout.TotalMilliseconds,
                Handshake = Handshake.XOnXOff,
                ReadBufferSize = 65536,
                WriteBufferSize = 65536,
            };

            _serialPort.Open();

            if (!_serialPort.IsOpen)
            {
                TransitionState(ConnectionState.Faulted, "Serial port failed to open");
                return Task.FromResult(new ConnectionResult { Success = false, ErrorMessage = $"Failed to open {portName}" });
            }

            // Flush any stale data
            _serialPort.DiscardInBuffer();
            _serialPort.DiscardOutBuffer();

            TransitionState(ConnectionState.Connected);
            _logger.LogInformation("Serial connected on {Port} at {BaudRate} baud", portName, baudRate);
            return Task.FromResult(new ConnectionResult { Success = true });
        }
        catch (Exception ex)
        {
            TransitionState(ConnectionState.Faulted, ex.Message);
            _logger.LogError(ex, "Serial connection failed");
            return Task.FromResult(new ConnectionResult { Success = false, ErrorMessage = ex.Message });
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        ClosePort();
        TransitionState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task<bool> PingAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_state == ConnectionState.Connected && _serialPort?.IsOpen == true);
    }

    public Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
        => throw new NotSupportedException("Serial transfer driver does not support tag read operations. Use file transfer methods.");

    public Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
        => throw new NotSupportedException("Serial transfer driver does not support tag read operations. Use file transfer methods.");

    public Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
        => throw new NotSupportedException("Serial transfer driver does not support tag write operations. Use file transfer methods.");

    public async Task<TransferProgressResult> UploadProgramAsync(
        Stream source, NCProgramMetadata metadata,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var transferId = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();

        try
        {
            var totalBytes = metadata.FileSize ?? source.Length;
            _logger.LogInformation("Serial upload started: {FileName} ({TotalBytes} bytes)", metadata.FileName, totalBytes);

            long transferred = 0;
            ushort seqNo = 0;
            var buffer = new byte[MaxChunkSize];

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var read = await source.ReadAsync(buffer.AsMemory(0, MaxChunkSize), ct);
                if (read == 0) break;

                var chunk = buffer[..read];
                var sent = false;

                for (var retry = 0; retry < MaxRetries && !sent; retry++)
                {
                    ct.ThrowIfCancellationRequested();

                    var frame = BuildFrame(seqNo, chunk);
                    _serialPort!.Write(frame, 0, frame.Length);

                    // Wait for ACK/NAK
                    var ack = await WaitForAckAsync(ct);
                    if (ack == ACK)
                    {
                        sent = true;
                    }
                    else
                    {
                        _logger.LogWarning("NAK received for seq {SeqNo}, retry {Retry}/{MaxRetries}",
                            seqNo, retry + 1, MaxRetries);
                        await Task.Delay(100 * (retry + 1), ct); // Backoff
                    }
                }

                if (!sent)
                {
                    sw.Stop();
                    var msg = $"Failed to send chunk seq {seqNo} after {MaxRetries} retries";
                    _logger.LogError(msg);
                    return new TransferProgressResult
                    {
                        Success = false,
                        TransferId = transferId,
                        BytesTransferred = transferred,
                        Duration = sw.Elapsed,
                        ErrorMessage = msg
                    };
                }

                transferred += read;
                seqNo++;
                progress?.Report(new TransferProgress { BytesTransferred = transferred, TotalBytes = totalBytes });
            }

            // Send end-of-transfer marker: zero-length frame
            var eofFrame = BuildFrame(seqNo, []);
            _serialPort!.Write(eofFrame, 0, eofFrame.Length);
            await WaitForAckAsync(ct);

            sw.Stop();
            _logger.LogInformation("Serial upload completed: {FileName} ({Bytes} bytes) in {Duration}", metadata.FileName, transferred, sw.Elapsed);

            return new TransferProgressResult
            {
                Success = true,
                TransferId = transferId,
                BytesTransferred = transferred,
                Duration = sw.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new TransferProgressResult
            {
                Success = false,
                TransferId = transferId,
                BytesTransferred = 0,
                Duration = sw.Elapsed,
                ErrorMessage = "Upload cancelled"
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Serial upload failed for {FileName}", metadata.FileName);
            return new TransferProgressResult
            {
                Success = false,
                TransferId = transferId,
                BytesTransferred = 0,
                Duration = sw.Elapsed,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<TransferProgressResult> DownloadProgramAsync(
        string remotePath, Stream destination,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var transferId = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Serial download started: {RemotePath}", remotePath);

            // Send download request command — a simple control frame indicating the file to fetch
            var requestPayload = System.Text.Encoding.UTF8.GetBytes(remotePath);
            var requestFrame = BuildFrame(0xFFFF, requestPayload); // 0xFFFF = download request marker
            _serialPort!.Write(requestFrame, 0, requestFrame.Length);

            long transferred = 0;
            ushort expectedSeq = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var (seqNo, data, valid) = await ReadFrameAsync(ct);

                if (!valid)
                {
                    // Send NAK, ask for retransmission
                    _serialPort.Write([NAK], 0, 1);
                    continue;
                }

                // End-of-transfer marker
                if (data.Length == 0)
                {
                    _serialPort.Write([ACK], 0, 1);
                    break;
                }

                if (seqNo != expectedSeq)
                {
                    _logger.LogWarning("Sequence mismatch: expected {Expected}, got {Actual}", expectedSeq, seqNo);
                    _serialPort.Write([NAK], 0, 1);
                    continue;
                }

                await destination.WriteAsync(data, ct);
                transferred += data.Length;
                expectedSeq++;

                _serialPort.Write([ACK], 0, 1);

                // Total is unknown for serial; report transferred bytes with 0 total
                progress?.Report(new TransferProgress { BytesTransferred = transferred, TotalBytes = 0 });
            }

            sw.Stop();
            _logger.LogInformation("Serial download completed: ({Bytes} bytes) in {Duration}", transferred, sw.Elapsed);

            return new TransferProgressResult
            {
                Success = true,
                TransferId = transferId,
                BytesTransferred = transferred,
                Duration = sw.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new TransferProgressResult
            {
                Success = false,
                TransferId = transferId,
                BytesTransferred = 0,
                Duration = sw.Elapsed,
                ErrorMessage = "Download cancelled"
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Serial download failed for {RemotePath}", remotePath);
            return new TransferProgressResult
            {
                Success = false,
                TransferId = transferId,
                BytesTransferred = 0,
                Duration = sw.Elapsed,
                ErrorMessage = ex.Message
            };
        }
    }

    public Task<TransferProgressResult> ResumeUploadAsync(
        string transferId, string remotePath, Stream source, long offset,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "Serial transfer does not support breakpoint resume. " +
            "Re-send the entire file using UploadProgramAsync.");
    }

    public ValueTask DisposeAsync()
    {
        ClosePort();
        if (_state != ConnectionState.Disconnected)
            TransitionState(ConnectionState.Disconnected);
        return ValueTask.CompletedTask;
    }

    // === Frame protocol helpers ===

    /// <summary>
    /// Builds a frame: [STX(1)] [SeqNo(2)] [Length(2)] [Data(N)] [CRC16(2)] [ETX(1)]
    /// </summary>
    private static byte[] BuildFrame(ushort seqNo, ReadOnlySpan<byte> data)
    {
        var frameLen = 1 + 2 + 2 + data.Length + 2 + 1; // STX + SeqNo + Len + Data + CRC + ETX
        var frame = new byte[frameLen];
        var offset = 0;

        frame[offset++] = STX;

        // SeqNo big-endian
        frame[offset++] = (byte)(seqNo >> 8);
        frame[offset++] = (byte)(seqNo & 0xFF);

        // Length big-endian
        var length = (ushort)data.Length;
        frame[offset++] = (byte)(length >> 8);
        frame[offset++] = (byte)(length & 0xFF);

        // Data
        data.CopyTo(frame.AsSpan(offset));
        offset += data.Length;

        // CRC-16/CCITT over SeqNo + Length + Data
        var crc = ComputeCrc16(frame.AsSpan(1, 4 + data.Length));
        frame[offset++] = (byte)(crc >> 8);
        frame[offset++] = (byte)(crc & 0xFF);

        frame[offset] = ETX;

        return frame;
    }

    /// <summary>
    /// Reads a complete frame from the serial port.
    /// Returns (seqNo, data, isValid).
    /// </summary>
    private async Task<(ushort seqNo, byte[] data, bool valid)> ReadFrameAsync(CancellationToken ct)
    {
        try
        {
            // Wait for STX
            var stx = await ReadByteWithTimeoutAsync(ct);
            if (stx != STX)
                return (0, [], false);

            // Read SeqNo (2 bytes)
            var seqHi = await ReadByteWithTimeoutAsync(ct);
            var seqLo = await ReadByteWithTimeoutAsync(ct);
            var seqNo = (ushort)((seqHi << 8) | seqLo);

            // Read Length (2 bytes)
            var lenHi = await ReadByteWithTimeoutAsync(ct);
            var lenLo = await ReadByteWithTimeoutAsync(ct);
            var length = (ushort)((lenHi << 8) | lenLo);

            if (length > MaxChunkSize)
                return (seqNo, [], false);

            // Read Data
            var data = new byte[length];
            var bytesRead = 0;
            while (bytesRead < length)
            {
                ct.ThrowIfCancellationRequested();
                var b = await ReadByteWithTimeoutAsync(ct);
                data[bytesRead++] = (byte)b;
            }

            // Read CRC (2 bytes)
            var crcHi = await ReadByteWithTimeoutAsync(ct);
            var crcLo = await ReadByteWithTimeoutAsync(ct);
            var receivedCrc = (ushort)((crcHi << 8) | crcLo);

            // Read ETX
            var etx = await ReadByteWithTimeoutAsync(ct);
            if (etx != ETX)
                return (seqNo, data, false);

            // Verify CRC: compute over SeqNo + Length + Data
            var checksumPayload = new byte[4 + length];
            checksumPayload[0] = (byte)seqHi;
            checksumPayload[1] = (byte)seqLo;
            checksumPayload[2] = (byte)lenHi;
            checksumPayload[3] = (byte)lenLo;
            Array.Copy(data, 0, checksumPayload, 4, length);

            var computedCrc = ComputeCrc16(checksumPayload);
            if (computedCrc != receivedCrc)
            {
                _logger.LogWarning("CRC mismatch on seq {SeqNo}: expected 0x{Expected:X4}, got 0x{Received:X4}",
                    seqNo, computedCrc, receivedCrc);
                return (seqNo, data, false);
            }

            return (seqNo, data, true);
        }
        catch (TimeoutException)
        {
            return (0, [], false);
        }
    }

    private async Task<byte> WaitForAckAsync(CancellationToken ct)
    {
        try
        {
            return (byte)await ReadByteWithTimeoutAsync(ct);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timeout waiting for ACK/NAK");
            return NAK;
        }
    }

    private Task<int> ReadByteWithTimeoutAsync(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            // SerialPort.ReadByte() is blocking, respects ReadTimeout
            var b = _serialPort!.ReadByte();
            if (b == -1) throw new TimeoutException("Serial port read returned -1");
            return b;
        }, ct);
    }

    /// <summary>
    /// CRC-16/CCITT (polynomial 0x1021, init 0xFFFF).
    /// </summary>
    private static ushort ComputeCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
            {
                if ((crc & 0x8000) != 0)
                    crc = (ushort)((crc << 1) ^ 0x1021);
                else
                    crc <<= 1;
            }
        }
        return crc;
    }

    // === State management ===

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected || _serialPort is null || !_serialPort.IsOpen)
            throw new InvalidOperationException("Serial port is not connected. Call ConnectAsync first.");
    }

    private void TransitionState(ConnectionState newState, string? reason = null)
    {
        var old = _state;
        if (old == newState) return;
        _state = newState;
        StateChanged?.Invoke(this, new() { OldState = old, NewState = newState, Reason = reason });
    }

    private void ClosePort()
    {
        try
        {
            if (_serialPort is not null)
            {
                if (_serialPort.IsOpen)
                {
                    _serialPort.DiscardInBuffer();
                    _serialPort.DiscardOutBuffer();
                    _serialPort.Close();
                }
                _serialPort.Dispose();
                _serialPort = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing serial port");
        }
    }
}
