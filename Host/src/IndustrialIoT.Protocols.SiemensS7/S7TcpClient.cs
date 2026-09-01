namespace IndustrialIoT.Protocols.SiemensS7;

using System.Net.Sockets;

internal sealed class S7TcpClient : IAsyncDisposable
{
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private ushort _messageId = 1;
    private int _pduLength = 200;

    public async Task ConnectAsync(string host, int port, SiemensS7PlcType plcType, byte rack, byte slot, TimeSpan timeout, CancellationToken ct)
    {
        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(host, port, ct).AsTask().WaitAsync(timeout, ct);
        _stream = _tcpClient.GetStream();
        _stream.ReadTimeout = (int)timeout.TotalMilliseconds;
        _stream.WriteTimeout = (int)timeout.TotalMilliseconds;

        await SendAndReceiveAsync(BuildCotpConnectionRequest(plcType, rack, slot), ct);
        var setup = await SendAndReceiveAsync(BuildSetupCommunication(), ct);
        if (setup.Length >= 27)
            _pduLength = Math.Max(200, ReadUInt16(setup, setup.Length - 2) - 28);
    }

    public async Task<bool> PingAsync(CancellationToken ct) =>
        (await ReadAsync(S7Address.Parse("M0", S7DataLength.Byte), ct)).Success;

    public async Task<S7OperationResult<byte[]>> ReadAsync(S7Address address, CancellationToken ct)
    {
        var response = await SendAndReceiveAsync(BuildReadCommand(address), ct);
        return ParseReadResponse(response, address);
    }

    public async Task<S7OperationResult> WriteAsync(S7Address address, byte[] value, CancellationToken ct)
    {
        var response = await SendAndReceiveAsync(BuildWriteCommand(address, value), ct);
        return ParseWriteResponse(response);
    }

    public async Task DisconnectAsync()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcpClient?.Dispose(); } catch { }
        _stream = null;
        _tcpClient = null;
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private async Task<byte[]> SendAndReceiveAsync(byte[] command, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("S7 client is not connected.");
        await stream.WriteAsync(command, ct);
        await stream.FlushAsync(ct);
        return await ReceiveTpktAsync(stream, ct);
    }

    private static byte[] BuildCotpConnectionRequest(SiemensS7PlcType plcType, byte rack, byte slot)
    {
        byte connectionType = 0x01;
        byte destTsapLow = (byte)(rack * 0x20 + slot);
        if (plcType == SiemensS7PlcType.S300) destTsapLow = 0x02;
        if (plcType == SiemensS7PlcType.S400) destTsapLow = 0x03;
        if (plcType is SiemensS7PlcType.S200 or SiemensS7PlcType.S200Smart)
            return [0x03, 0x00, 0x00, 0x16, 0x11, 0xE0, 0x00, 0x00, 0x00, 0x01, 0x00, 0xC1, 0x02, 0x10, 0x00, 0xC2, 0x02, 0x03, 0x00, 0xC0, 0x01, 0x0A];
        return [0x03, 0x00, 0x00, 0x16, 0x11, 0xE0, 0x00, 0x00, 0x00, 0x01, 0x00, 0xC0, 0x01, 0x0A, 0xC1, 0x02, 0x01, 0x02, 0xC2, 0x02, connectionType, destTsapLow];
    }

    private static byte[] BuildSetupCommunication() =>
        [0x03, 0x00, 0x00, 0x19, 0x02, 0xF0, 0x80, 0x32, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x00, 0xF0, 0x00, 0x00, 0x01, 0x00, 0x01, 0x01, 0xE0];

    private static ushort ReadUInt16(byte[] bytes, int offset) =>
        (ushort)((bytes[offset] << 8) | bytes[offset + 1]);

    private byte[] BuildReadCommand(S7Address address)
    {
        var command = new byte[31];
        command[0] = 0x03; command[1] = 0x00; command[2] = 0x00; command[3] = 0x1F;
        command[4] = 0x02; command[5] = 0xF0; command[6] = 0x80;
        command[7] = 0x32; command[8] = 0x01; command[9] = 0x00; command[10] = 0x00;
        WriteUInt16(command, 11, NextMessageId());
        command[13] = 0x00; command[14] = 0x0E; command[15] = 0x00; command[16] = 0x00;
        command[17] = 0x04; command[18] = 0x01;
        WriteAddressItem(command, 19, address, address.Length, address.IsBit);
        return command;
    }

    private byte[] BuildWriteCommand(S7Address address, byte[] value)
    {
        var dataLength = address.IsBit ? 1 : value.Length * 8;
        var command = new byte[35 + value.Length];
        command[0] = 0x03; command[1] = 0x00; WriteUInt16(command, 2, (ushort)command.Length);
        command[4] = 0x02; command[5] = 0xF0; command[6] = 0x80;
        command[7] = 0x32; command[8] = 0x01; command[9] = 0x00; command[10] = 0x00;
        WriteUInt16(command, 11, NextMessageId());
        command[13] = 0x00; command[14] = 0x0E; WriteUInt16(command, 15, (ushort)(4 + value.Length));
        command[17] = 0x05; command[18] = 0x01;
        WriteAddressItem(command, 19, address, address.Length, address.IsBit);
        command[31] = 0x00; command[32] = address.IsBit ? (byte)0x03 : (byte)0x04;
        WriteUInt16(command, 33, (ushort)dataLength);
        value.CopyTo(command, 35);
        return command;
    }

    private static void WriteAddressItem(byte[] command, int offset, S7Address address, ushort length, bool isBit)
    {
        command[offset] = 0x12; command[offset + 1] = 0x0A; command[offset + 2] = 0x10;
        command[offset + 3] = isBit ? (byte)0x01 : GetTransportSize(address.Area);
        WriteUInt16(command, offset + 4, length);
        WriteUInt16(command, offset + 6, address.DbNumber);
        command[offset + 8] = (byte)address.Area;
        command[offset + 9] = (byte)(address.BitAddress >> 16);
        command[offset + 10] = (byte)(address.BitAddress >> 8);
        command[offset + 11] = (byte)address.BitAddress;
    }

    private static byte GetTransportSize(S7MemoryArea area) =>
        area is S7MemoryArea.Counter or S7MemoryArea.Timer ? (byte)0x1C : (byte)0x02;

    private ushort NextMessageId() => _messageId++ == 0 ? _messageId++ : (ushort)(_messageId - 1);

    private static void WriteUInt16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)(value >> 8);
        bytes[offset + 1] = (byte)value;
    }

    private static S7OperationResult<byte[]> ParseReadResponse(byte[] response, S7Address address)
    {
        if (response.Length < 25)
            return S7OperationResult<byte[]>.Fail($"S7 read response too short: {response.Length} bytes.");

        var dataOffset = 21;
        if (response[dataOffset] != 0xFF)
            return S7OperationResult<byte[]>.Fail(GetS7Error(response[dataOffset]));

        var transportSize = response[dataOffset + 1];
        var length = ReadUInt16(response, dataOffset + 2);
        var byteLength = transportSize == 0x03 ? 1 : (length + 7) / 8;
        if (response.Length < dataOffset + 4 + byteLength)
            return S7OperationResult<byte[]>.Fail("S7 read response data is truncated.");

        var data = response.AsSpan(dataOffset + 4, byteLength).ToArray();
        if (address.IsBit)
            data = [data[0]];
        return S7OperationResult<byte[]>.Ok(data);
    }

    private static S7OperationResult ParseWriteResponse(byte[] response)
    {
        if (response.Length < 22)
            return S7OperationResult.Fail($"S7 write response too short: {response.Length} bytes.");
        return response[21] == 0xFF ? S7OperationResult.Ok() : S7OperationResult.Fail(GetS7Error(response[21]));
    }

    private static string GetS7Error(byte code) => code switch
    {
        0x05 => "S7 address is out of PLC range.",
        0x06 => "S7 data type is not supported by PLC.",
        0x0A => "S7 object does not exist or CPU access protection rejected it.",
        _ => $"S7 operation failed with error 0x{code:X2}.",
    };

    private static async Task<byte[]> ReceiveTpktAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await ReadExactAsync(stream, header, ct);
        var length = ReadUInt16(header, 2);
        if (length < 4) throw new InvalidOperationException($"Invalid TPKT length {length}.");
        var body = new byte[length - 4];
        await ReadExactAsync(stream, body, ct);
        var packet = new byte[length];
        header.CopyTo(packet, 0);
        body.CopyTo(packet, 4);
        return packet;
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0) throw new IOException("Remote S7 endpoint closed the connection.");
            offset += read;
        }
    }
}
