using System.Net;
using System.Net.Sockets;

namespace IottDevMock.Mock;

/// <summary>
/// 极简 Modbus TCP 从站（方案 C：协议端点模拟）。实现常用功能码 1/2/3/4/5/6/15/16，
/// 供真实 IndustrialIoT.Host 的 ModbusTCP 驱动直连，从而产生真实（非伪造）通讯。
/// 数据确定性初始化；写入会更新内存镜像。
/// </summary>
public sealed class ModbusTcpSlave
{
    private readonly int _port;
    private readonly ushort[] _holding = new ushort[0x10000];
    private readonly ushort[] _input = new ushort[0x10000];
    private readonly bool[] _coils = new bool[0x10000];
    private readonly bool[] _discrete = new bool[0x10000];

    public ModbusTcpSlave(int port, int seed)
    {
        _port = port;
        var rnd = new Random(seed);
        for (var i = 0; i < 1000; i++)
        {
            _holding[i] = (ushort)rnd.Next(0, 10000);
            _input[i] = (ushort)rnd.Next(0, 10000);
            _coils[i] = rnd.Next(0, 2) == 1;
            _discrete[i] = rnd.Next(0, 2) == 1;
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
        var header = new byte[7];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, header, 7, ct)) break;
                var length = (header[4] << 8) | header[5];
                var pduLength = length - 1;
                if (pduLength is <= 0 or > 260) break;
                var pdu = new byte[pduLength];
                if (!await ReadExactAsync(stream, pdu, pduLength, ct)) break;

                var responsePdu = Process(header[6], pdu);

                var response = new byte[7 + responsePdu.Length];
                Array.Copy(header, 0, response, 0, 6); // 回显事务号 + 协议号
                var respLength = responsePdu.Length + 1;
                response[4] = (byte)(respLength >> 8);
                response[5] = (byte)(respLength & 0xFF);
                response[6] = header[6];
                Array.Copy(responsePdu, 0, response, 7, responsePdu.Length);

                await stream.WriteAsync(response, ct);
            }
        }
        catch (IOException) { /* 客户端断开 */ }
        catch (OperationCanceledException) { }
    }

    private byte[] Process(byte unitId, byte[] pdu)
    {
        var fc = pdu[0];
        try
        {
            switch (fc)
            {
                case 1: // Read Coils
                case 2: // Read Discrete Inputs
                {
                    var start = ReadU16(pdu, 1);
                    var qty = ReadU16(pdu, 3);
                    if (qty is < 1 or > 2000 || start + qty > 0x10000) return Exception(fc, 0x02);
                    var source = fc == 1 ? _coils : _discrete;
                    var byteCount = (qty + 7) / 8;
                    var resp = new byte[2 + byteCount];
                    resp[0] = fc;
                    resp[1] = (byte)byteCount;
                    for (var i = 0; i < qty; i++)
                        if (source[start + i]) resp[2 + (i / 8)] |= (byte)(1 << (i % 8));
                    return resp;
                }
                case 3: // Read Holding Registers
                case 4: // Read Input Registers
                {
                    var start = ReadU16(pdu, 1);
                    var qty = ReadU16(pdu, 3);
                    if (qty is < 1 or > 125 || start + qty > 0x10000) return Exception(fc, 0x02);
                    var source = fc == 3 ? _holding : _input;
                    var resp = new byte[2 + qty * 2];
                    resp[0] = fc;
                    resp[1] = (byte)(qty * 2);
                    for (var i = 0; i < qty; i++)
                    {
                        resp[2 + i * 2] = (byte)(source[start + i] >> 8);
                        resp[3 + i * 2] = (byte)(source[start + i] & 0xFF);
                    }
                    return resp;
                }
                case 5: // Write Single Coil
                {
                    var addr = ReadU16(pdu, 1);
                    var value = ReadU16(pdu, 3);
                    if (value is not (0x0000 or 0xFF00)) return Exception(fc, 0x03);
                    _coils[addr] = value == 0xFF00;
                    return [fc, pdu[1], pdu[2], pdu[3], pdu[4]];
                }
                case 6: // Write Single Register
                {
                    var addr = ReadU16(pdu, 1);
                    _holding[addr] = (ushort)ReadU16(pdu, 3);
                    return [fc, pdu[1], pdu[2], pdu[3], pdu[4]];
                }
                case 15: // Write Multiple Coils
                {
                    var start = ReadU16(pdu, 1);
                    var qty = ReadU16(pdu, 3);
                    if (qty is < 1 or > 1968 || start + qty > 0x10000) return Exception(fc, 0x02);
                    var byteCount = pdu[5];
                    if (byteCount != (qty + 7) / 8) return Exception(fc, 0x03);
                    for (var i = 0; i < qty; i++)
                        _coils[start + i] = (pdu[6 + (i / 8)] & (1 << (i % 8))) != 0;
                    return [fc, pdu[1], pdu[2], pdu[3], pdu[4]];
                }
                case 16: // Write Multiple Registers
                {
                    var start = ReadU16(pdu, 1);
                    var qty = ReadU16(pdu, 3);
                    if (qty is < 1 or > 123 || start + qty > 0x10000) return Exception(fc, 0x02);
                    var byteCount = pdu[5];
                    if (byteCount != qty * 2) return Exception(fc, 0x03);
                    for (var i = 0; i < qty; i++)
                        _holding[start + i] = (ushort)((pdu[6 + i * 2] << 8) | pdu[7 + i * 2]);
                    return [fc, pdu[1], pdu[2], pdu[3], pdu[4]];
                }
                default:
                    return Exception(fc, 0x01); // Illegal Function
            }
        }
        catch (IndexOutOfRangeException) { return Exception(fc, 0x02); }
    }

    private static ushort ReadU16(byte[] buffer, int offset) => (ushort)((buffer[offset] << 8) | buffer[offset + 1]);

    private static byte[] Exception(byte fc, byte code) => [(byte)(fc | 0x80), code];

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
