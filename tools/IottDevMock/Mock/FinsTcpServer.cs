using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace IottDevMock.Mock;

/// <summary>
/// 极简 FINS/TCP 从站（方案 C）。实现 FINS/TCP 帧（magic "FINS" + 命令 0/2）与 FINS 内存区读写
/// （命令 0101 读 / 0102 写），供真实 IndustrialIoT.Host 的 FINS 驱动直连。字访问；位访问返回不支持。
/// </summary>
public sealed class FinsTcpServer
{
    private const int WordCapacity = 0x10000;
    private readonly int _port;
    private readonly Dictionary<byte, ushort[]> _areas;

    public FinsTcpServer(int port, int seed)
    {
        _port = port;
        var rnd = new Random(seed);
        _areas = new Dictionary<byte, ushort[]>();
        // DM 0x82, CIO 0xB0, WR 0xB1, HR 0xB2, AR 0xB3
        foreach (var code in new byte[] { 0x82, 0xB0, 0xB1, 0xB2, 0xB3 })
        {
            var area = new ushort[WordCapacity];
            for (var i = 0; i < 2000; i++) area[i] = (ushort)rnd.Next(0, 10000);
            _areas[code] = area;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
        listener.Stop();
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var stream = client.GetStream();
        var header = new byte[16];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, header, 16, ct)) break;
                if (!(header[0] == 'F' && header[1] == 'I' && header[2] == 'N' && header[3] == 'S')) break;

                var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4));
                var command = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4));
                var payloadLength = length - 8;
                if (payloadLength is < 0 or > 65536) break;
                var payload = new byte[payloadLength];
                if (payloadLength > 0 && !await ReadExactAsync(stream, payload, payloadLength, ct)) break;

                if (command == 0) // Node address data send
                {
                    var resp = new byte[20];
                    WriteHeader(resp, 0, 12, 0, 0);
                    resp[16] = 0; resp[17] = 0; resp[18] = 1; resp[19] = 0;
                    await stream.WriteAsync(resp, ct);
                    continue;
                }
                if (command == 2) // FINS frame send
                {
                    var responseFrame = ProcessFinsFrame(payload);
                    var resp = new byte[16 + responseFrame.Length];
                    WriteHeader(resp, 0, 8 + responseFrame.Length, 2, 0);
                    Array.Copy(responseFrame, 0, resp, 16, responseFrame.Length);
                    await stream.WriteAsync(resp, ct);
                    continue;
                }
                var err = new byte[16];
                WriteHeader(err, 0, 8, command, 0x00000001);
                await stream.WriteAsync(err, ct);
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
    }

    private static void WriteHeader(byte[] buffer, int offset, int length, int command, int error)
    {
        buffer[offset] = (byte)'F'; buffer[offset + 1] = (byte)'I'; buffer[offset + 2] = (byte)'N'; buffer[offset + 3] = (byte)'S';
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset + 4, 4), length);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset + 8, 4), command);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset + 12, 4), error);
    }

    private byte[] ProcessFinsFrame(byte[] frame)
    {
        if (frame.Length < 12) return Response(frame, 0x01, 0x01, 0x0002);
        var mrc = frame[10];
        var src = frame[11];
        var rest = frame.AsSpan(12);
        if (mrc == 0x01 && src == 0x01) return MemoryRead(frame, rest);
        if (mrc == 0x01 && src == 0x02) return MemoryWrite(frame, rest);
        return Response(frame, mrc, src, 0x0001); // command not supported
    }

    private byte[] MemoryRead(byte[] frame, ReadOnlySpan<byte> rest)
    {
        if (rest.Length < 6) return Response(frame, 0x01, 0x01, 0x0002);
        var areaCode = rest[0];
        var address = BinaryPrimitives.ReadUInt16BigEndian(rest.Slice(1, 2));
        var bit = rest[3];
        var count = BinaryPrimitives.ReadUInt16BigEndian(rest.Slice(4, 2));
        if (bit != 0) return Response(frame, 0x01, 0x01, 0x1103); // 位访问未实现
        if (count == 0 || !_areas.TryGetValue(areaCode, out var area) || address + count > area.Length)
            return Response(frame, 0x01, 0x01, 0x1101);

        var resp = new byte[14 + count * 2];
        Array.Copy(frame, 0, resp, 0, 10);
        resp[10] = 0x01; resp[11] = 0x01;
        resp[12] = 0x00; resp[13] = 0x00;
        for (var i = 0; i < count; i++)
            BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(14 + i * 2, 2), area[address + i]);
        return resp;
    }

    private byte[] MemoryWrite(byte[] frame, ReadOnlySpan<byte> rest)
    {
        if (rest.Length < 6) return Response(frame, 0x01, 0x02, 0x0002);
        var areaCode = rest[0];
        var address = BinaryPrimitives.ReadUInt16BigEndian(rest.Slice(1, 2));
        var bit = rest[3];
        var count = BinaryPrimitives.ReadUInt16BigEndian(rest.Slice(4, 2));
        if (bit != 0) return Response(frame, 0x01, 0x02, 0x1103);
        if (!_areas.TryGetValue(areaCode, out var area) || address + count > area.Length)
            return Response(frame, 0x01, 0x02, 0x1101);
        var data = rest[6..];
        if (data.Length < count * 2) return Response(frame, 0x01, 0x02, 0x0002);
        for (var i = 0; i < count; i++)
            area[address + i] = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i * 2, 2));
        return Response(frame, 0x01, 0x02, 0x0000);
    }

    /// <summary>FINS 响应：头(10) + MRC + SRC + endCode(2)。</summary>
    private static byte[] Response(byte[] frame, byte mrc, byte src, ushort endCode)
    {
        var resp = new byte[14];
        Array.Copy(frame, 0, resp, 0, Math.Min(10, frame.Length));
        resp[10] = mrc; resp[11] = src;
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(12, 2), endCode);
        return resp;
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}
