namespace IndustrialIoT.Protocols.FINS;

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// Low-level FINS/TCP protocol helper — frame building, parsing, and constants.
/// Reference: Omron W421-E1-04 FINS Command Reference Manual.
/// </summary>
internal static class FinsProtocol
{
    // ── FINS/TCP header constants ──────────────────────────────────────

    internal static readonly byte[] FinsMagic = "FINS"u8.ToArray(); // 46 49 4E 53

    internal const int TcpHeaderLength = 16;

    // FINS/TCP command codes (in the TCP header, NOT the FINS frame MRC/SRC)
    internal const uint TcpCmdClientNodeRequest  = 0x00000000;
    internal const uint TcpCmdServerNodeResponse = 0x00000001;
    internal const uint TcpCmdFinsFrame          = 0x00000002;

    // ── FINS frame offsets (inside payload, after TCP header) ──────────

    internal const int FinsHeaderLength = 12; // ICF..SID (10 bytes) + MRC + SRC

    // ── Memory area codes (word / bit) ─────────────────────────────────

    internal static readonly Dictionary<string, (byte WordCode, byte BitCode, int MaxAddress)> MemoryAreas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CIO"] = (0x30, 0xB0, 6143),
        ["WR"]  = (0x31, 0xB1, 511),
        ["HR"]  = (0x32, 0xB2, 511),
        ["AR"]  = (0x33, 0xB3, 959),
        ["DM"]  = (0x02, 0x82, 32767),
    };

    // ── FINS command MRC/SRC ───────────────────────────────────────────

    internal const byte MrcMemoryAreaRead  = 0x01;
    internal const byte SrcMemoryAreaRead  = 0x01;
    internal const byte MrcMemoryAreaWrite = 0x01;
    internal const byte SrcMemoryAreaWrite = 0x02;

    // ── Address parsing ────────────────────────────────────────────────

    /// <summary>
    /// Parse a FINS address string such as "DM100", "CIO50.2", "WR0".
    /// Returns (areaPrefix, wordAddress, bitAddress).
    /// </summary>
    internal static (string Area, ushort Word, byte Bit) ParseAddress(string address)
    {
        // Find boundary between alpha prefix and numeric part
        int numStart = -1;
        for (int i = 0; i < address.Length; i++)
        {
            if (char.IsDigit(address[i]))
            {
                numStart = i;
                break;
            }
        }

        if (numStart <= 0)
            throw new FormatException($"Invalid FINS address '{address}': expected format like DM100, CIO0.5");

        string area = address[..numStart].ToUpperInvariant();
        string numPart = address[numStart..];

        if (!MemoryAreas.ContainsKey(area))
            throw new FormatException($"Unknown FINS memory area '{area}'. Supported: {string.Join(", ", MemoryAreas.Keys)}");

        ushort word;
        byte bit = 0;
        int dotIndex = numPart.IndexOf('.');
        if (dotIndex >= 0)
        {
            word = ushort.Parse(numPart[..dotIndex]);
            bit = byte.Parse(numPart[(dotIndex + 1)..]);
            if (bit > 15)
                throw new FormatException($"Bit offset must be 0-15, got {bit}");
        }
        else
        {
            word = ushort.Parse(numPart);
        }

        var maxAddr = MemoryAreas[area].MaxAddress;
        if (word > maxAddr)
            throw new FormatException($"Address {area}{word} exceeds maximum ({maxAddr})");

        return (area, word, bit);
    }

    // ── Frame builders ─────────────────────────────────────────────────

    /// <summary>Build the initial FINS/TCP node-address negotiation request.</summary>
    internal static byte[] BuildNodeAddressRequest(byte clientNodeAddress)
    {
        var frame = new byte[TcpHeaderLength + 4]; // header + 4 bytes payload (client node)
        WriteTcpHeader(frame, TcpCmdClientNodeRequest, payloadLength: 4);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(TcpHeaderLength), clientNodeAddress);
        return frame;
    }

    /// <summary>
    /// Build a FINS Memory Area Read command (MRC=01 SRC=01).
    /// </summary>
    internal static byte[] BuildReadCommand(
        byte destNetwork, byte destNode, byte destUnit,
        byte srcNetwork, byte srcNode, byte srcUnit,
        byte sid,
        byte memoryAreaCode, ushort startWord, byte startBit, ushort wordCount)
    {
        // FINS payload: header(10) + MRC(1) + SRC(1) + areaCode(1) + addr(3) + count(2) = 18
        int finsPayloadLen = FinsHeaderLength + 6; // 12 + 6 = 18
        var frame = new byte[TcpHeaderLength + finsPayloadLen];

        WriteTcpHeader(frame, TcpCmdFinsFrame, finsPayloadLen);
        WriteFinsHeader(frame.AsSpan(TcpHeaderLength),
            destNetwork, destNode, destUnit,
            srcNetwork, srcNode, srcUnit,
            sid, MrcMemoryAreaRead, SrcMemoryAreaRead);

        int offset = TcpHeaderLength + FinsHeaderLength;
        frame[offset++] = memoryAreaCode;              // Memory area code
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset), startWord);
        offset += 2;
        frame[offset++] = startBit;                    // Start bit
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset), wordCount);

        return frame;
    }

    /// <summary>
    /// Build a FINS Memory Area Write command (MRC=01 SRC=02).
    /// </summary>
    internal static byte[] BuildWriteCommand(
        byte destNetwork, byte destNode, byte destUnit,
        byte srcNetwork, byte srcNode, byte srcUnit,
        byte sid,
        byte memoryAreaCode, ushort startWord, byte startBit, ushort wordCount,
        ReadOnlySpan<byte> data)
    {
        // FINS payload: header(12) + areaCode(1) + addr(3) + count(2) + data
        int finsPayloadLen = FinsHeaderLength + 6 + data.Length;
        var frame = new byte[TcpHeaderLength + finsPayloadLen];

        WriteTcpHeader(frame, TcpCmdFinsFrame, finsPayloadLen);
        WriteFinsHeader(frame.AsSpan(TcpHeaderLength),
            destNetwork, destNode, destUnit,
            srcNetwork, srcNode, srcUnit,
            sid, MrcMemoryAreaWrite, SrcMemoryAreaWrite);

        int offset = TcpHeaderLength + FinsHeaderLength;
        frame[offset++] = memoryAreaCode;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset), startWord);
        offset += 2;
        frame[offset++] = startBit;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset), wordCount);
        offset += 2;
        data.CopyTo(frame.AsSpan(offset));

        return frame;
    }

    // ── Response parsing ───────────────────────────────────────────────

    /// <summary>Read and validate a full FINS/TCP frame from the stream.</summary>
    internal static async Task<(uint Command, byte[] Payload)> ReadTcpFrameAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[TcpHeaderLength];
        await ReadExactAsync(stream, header, ct);

        if (header[0] != 'F' || header[1] != 'I' || header[2] != 'N' || header[3] != 'S')
            throw new InvalidOperationException("Invalid FINS/TCP header magic");

        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
        uint command = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
        uint errorCode = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));

        if (errorCode != 0)
            throw new InvalidOperationException($"FINS/TCP header error: 0x{errorCode:X8}");

        // length includes the command(4) + errorCode(4) bytes already accounted in header
        int payloadLength = length - 8;
        var payload = Array.Empty<byte>();
        if (payloadLength > 0)
        {
            payload = new byte[payloadLength];
            await ReadExactAsync(stream, payload, ct);
        }

        return (command, payload);
    }

    /// <summary>Parse node address from the server response to our negotiation request.</summary>
    internal static (byte ServerNode, byte ClientNode) ParseNodeAddressResponse(byte[] payload)
    {
        if (payload.Length < 8)
            throw new InvalidOperationException("Node address response too short");

        // payload: [0..3] = server node, [4..7] = client node
        byte serverNode = (byte)BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0));
        byte clientNode = (byte)BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(4));
        return (serverNode, clientNode);
    }

    /// <summary>
    /// Extract FINS response code (MRES + SRES) and data from a FINS frame payload.
    /// Returns (mainCode, subCode, data after response codes).
    /// </summary>
    internal static (byte MainResponse, byte SubResponse, ReadOnlyMemory<byte> Data) ParseFinsResponse(byte[] finsPayload)
    {
        // FINS header: ICF(1) RSV(1) GCT(1) DNA(1) DA1(1) DA2(1) SNA(1) SA1(1) SA2(1) SID(1) MRC(1) SRC(1)
        // Response: MRES(1) SRES(1) + data
        if (finsPayload.Length < FinsHeaderLength + 2)
            throw new InvalidOperationException("FINS response frame too short");

        byte mres = finsPayload[FinsHeaderLength];
        byte sres = finsPayload[FinsHeaderLength + 1];
        var data = finsPayload.AsMemory(FinsHeaderLength + 2);
        return (mres, sres, data);
    }

    /// <summary>Translate a FINS error code pair to a human-readable message.</summary>
    internal static string GetErrorMessage(byte mainCode, byte subCode)
    {
        return (mainCode, subCode) switch
        {
            (0x00, 0x00) => "Normal completion",
            (0x00, 0x01) => "Service was interrupted",
            (0x01, _)    => $"Local node error (sub: 0x{subCode:X2})",
            (0x02, _)    => $"Destination node error (sub: 0x{subCode:X2})",
            (0x03, _)    => $"Communications controller error (sub: 0x{subCode:X2})",
            (0x04, _)    => $"Not executable (sub: 0x{subCode:X2})",
            (0x05, _)    => $"Routing error (sub: 0x{subCode:X2})",
            (0x10, _)    => $"Command format error (sub: 0x{subCode:X2})",
            (0x11, _)    => $"Parameter error (sub: 0x{subCode:X2})",
            (0x20, _)    => $"Read not possible (sub: 0x{subCode:X2})",
            (0x21, _)    => $"Write not possible (sub: 0x{subCode:X2})",
            (0x22, _)    => $"Not executable in current mode (sub: 0x{subCode:X2})",
            (0x23, _)    => $"No such device (sub: 0x{subCode:X2})",
            (0x24, _)    => $"Start/stop not possible (sub: 0x{subCode:X2})",
            (0x25, _)    => $"Unit error (sub: 0x{subCode:X2})",
            (0x26, _)    => $"Command error (sub: 0x{subCode:X2})",
            (0x30, _)    => $"Access right error (sub: 0x{subCode:X2})",
            (0x40, _)    => $"Abort (sub: 0x{subCode:X2})",
            _            => $"Unknown FINS error (main: 0x{mainCode:X2}, sub: 0x{subCode:X2})",
        };
    }

    // ── Internal helpers ───────────────────────────────────────────────

    private static void WriteTcpHeader(Span<byte> buffer, uint command, int payloadLength)
    {
        // 'FINS' magic
        buffer[0] = 0x46; buffer[1] = 0x49; buffer[2] = 0x4E; buffer[3] = 0x53;
        // Length = payloadLength + 8 (command 4 bytes + error code 4 bytes)
        BinaryPrimitives.WriteInt32BigEndian(buffer[4..], payloadLength + 8);
        // Command
        BinaryPrimitives.WriteUInt32BigEndian(buffer[8..], command);
        // Error code = 0
        BinaryPrimitives.WriteUInt32BigEndian(buffer[12..], 0);
    }

    private static void WriteFinsHeader(Span<byte> buffer,
        byte destNetwork, byte destNode, byte destUnit,
        byte srcNetwork, byte srcNode, byte srcUnit,
        byte sid, byte mrc, byte src)
    {
        buffer[0] = 0x80; // ICF: command, response required
        buffer[1] = 0x00; // RSV: reserved
        buffer[2] = 0x02; // GCT: gateway count (2 = allow 2 gateways)
        buffer[3] = destNetwork; // DNA
        buffer[4] = destNode;    // DA1
        buffer[5] = destUnit;    // DA2
        buffer[6] = srcNetwork;  // SNA
        buffer[7] = srcNode;     // SA1
        buffer[8] = srcUnit;     // SA2
        buffer[9] = sid;         // SID
        buffer[10] = mrc;        // MRC
        buffer[11] = src;        // SRC
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
                throw new IOException("FINS/TCP connection closed unexpectedly");
            offset += read;
        }
    }
}
