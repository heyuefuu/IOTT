namespace DeviceSimGateway.Services;

using System.Net;
using System.Net.Sockets;
using DeviceSimGateway.Core;

/// <summary>
/// 严格按 S7 通信（RFC1006/TPKT + ISO-TSAP COTP + S7comm）实现的 S7 服务端：
/// 支持 COTP CR→CC、Setup Communication、Read Var / Write Var（DB/M/I/Q，BIT/BYTE/WORD/DWORD/REAL）。
/// 数据区映射到共享数据区：DB→保持寄存器，M→输入寄存器，Q/线圈→输出，I/离散→输入。
/// </summary>
public sealed class SiemensS7Service : IDeviceService
{
    public string Key => "s7";
    public string Name => "Siemens S7";
    public string Description => "S7comm / ISO-TSAP 服务端（DB/M/I/Q，自研实现）";
    public int DefaultPort => 102;
    public bool Implemented => true;
    public ServiceState State { get; private set; } = ServiceState.Stopped;
    public string Message { get; private set; } = "";
    public int Port { get; private set; }

    private const ushort PduLength = 480;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private SimDataStore? _store;

    public Task StartAsync(int port, SimDataStore data, GeneratorConfig gen, CancellationToken ct)
    {
        _store = data; Port = port;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        State = ServiceState.Running;
        Message = $"监听 0.0.0.0:{port}（DB/M/I/Q）";
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        if (_loop is not null) { try { await _loop.WaitAsync(TimeSpan.FromSeconds(3), ct); } catch { } }
        _listener = null; _cts = null; _loop = null;
        if (State == ServiceState.Running) { State = ServiceState.Stopped; Message = "已停止"; }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        using (client)
        {
            var s = client.GetStream();
            try
            {
                if (!await HandshakeAsync(s, ct)) return;
                while (!ct.IsCancellationRequested)
                {
                    var frame = await ReadTpktAsync(s, ct);
                    if (frame is null) break;
                    var s7 = ExtractS7(frame);
                    if (s7 is null) continue;
                    if (_store!.Failure.ShouldFail())
                    {
                        if (_store.Failure.DropConnection) break;   // 随机失败：断链
                        continue;                                   // 随机失败：丢弃该帧（读超时）
                    }
                    var resp = HandlePdu(s7);
                    if (resp is null) continue;
                    await s.WriteAsync(WrapTpkt(resp), ct);
                    await s.FlushAsync(ct);
                }
            }
            catch { /* 客户端断开 */ }
        }
    }

    // ---------- COTP 握手 ----------
    private static async Task<bool> HandshakeAsync(NetworkStream s, CancellationToken ct)
    {
        // CR 通常为固定长度 22 字节的 TPKT
        var head = new byte[4];
        if (!await ReadExactAsync(s, head, 4, ct)) return false;
        if (head[0] != 0x03) return false;
        int len = (head[2] << 8) | head[3];
        var rest = new byte[Math.Max(0, len - 4)];
        if (rest.Length > 0 && !await ReadExactAsync(s, rest, rest.Length, ct)) return false;

        // 解析 CR 中的 C1(本地TSAP)/C2(远端TSAP)
        byte[] src = [0x01, 0x00], dst = [0x03, 0x01];
        var cr = rest;
        for (int i = 0; i + 3 < cr.Length; i++)
        {
            if (cr[i] == 0xC1 && cr[i + 1] == 0x02) src = new[] { cr[i + 2], cr[i + 3] };
            else if (cr[i] == 0xC2 && cr[i + 1] == 0x02) dst = new[] { cr[i + 2], cr[i + 3] };
        }
        // CC：交换 src/dst（我方 = 对端目的 TSAP）
        var cc = new byte[]
        {
            0x03, 0x00, 0x00, 0x16, 0x11, 0xD0, 0x00, 0x01, 0x00, 0x00, 0x00,
            0xC0, 0x01, 0x0A,
            0xC1, 0x02, dst[0], dst[1],
            0xC2, 0x02, src[0], src[1],
        };
        await s.WriteAsync(cc, ct); await s.FlushAsync(ct);
        return true;
    }

    // ---------- S7 PDU ----------
    private byte[]? HandlePdu(byte[] s7)
    {
        if (s7.Length < 10) return null;
        byte rosctr = s7[1];
        ushort pduRef = (ushort)((s7[4] << 8) | s7[5]);
        int paramLen = (s7[6] << 8) | s7[7];
        int dataLen = (s7[8] << 8) | s7[9];
        if (s7.Length < 10 + paramLen) return null;
        var param = s7.AsSpan(10, paramLen).ToArray();
        if (rosctr != 0x01 || param.Length < 1) return null;

        return param[0] switch
        {
            0xF0 => BuildSetupComm(pduRef, param),
            0x04 => BuildReadVar(pduRef, param, dataLen),
            0x05 => BuildWriteVar(pduRef, param, s7, paramLen, dataLen),
            0x07 => BuildUserData(pduRef, param),      // SZL 等：最小响应
            _ => BuildErrorAck(pduRef, param[0]),
        };
    }

    private static byte[] BuildSetupComm(ushort pduRef, byte[] param)
    {
        // 参数: F0 00 00 <maxAmqCaller> <maxAmqCallee> <pduLen>（Job 为 8 字节）
        ushort caller = param.Length >= 8 ? (ushort)((param[4] << 8) | param[5]) : (ushort)1;
        ushort callee = param.Length >= 8 ? (ushort)((param[6] << 8) | param[7]) : (ushort)1;
        var p = new byte[] { 0xF0, 0x00, 0x00, (byte)(caller >> 8), (byte)caller, (byte)(callee >> 8), (byte)callee, (byte)(PduLength >> 8), (byte)(PduLength & 0xFF) };
        return Ack(pduRef, p, Array.Empty<byte>());
    }

    private byte[] BuildReadVar(ushort pduRef, byte[] param, int dataLen)
    {
        if (param.Length < 2) return BuildErrorAck(pduRef, 0x04);
        int itemCount = param[1];
        var reqItems = ParseItems(param, 2, itemCount);
        var respParams = new List<byte> { 0x04, (byte)itemCount };
        var respData = new List<byte>();
        foreach (var it in reqItems)
        {
            var (ok, transport, data) = ReadItem(it);
            if (!ok)
            {
                respParams.Add(0x0A);                 // 访问错误
                respParams.Add(0x00); respParams.Add(0x00); respParams.Add(0x00);
                continue;
            }
            respParams.Add(0xFF);                     // success
            respParams.Add(transport);
            // 位按“位”计数，其余按“字节”计数
            int lenField = transport == 0x03 ? it.Count : data.Length * 8;
            respParams.Add((byte)(lenField >> 8)); respParams.Add((byte)lenField);
            respData.AddRange(data);
        }
        return Ack(pduRef, respParams.ToArray(), respData.ToArray());
    }

    private byte[] BuildWriteVar(ushort pduRef, byte[] param, byte[] s7, int paramLen, int dataLen)
    {
        if (param.Length < 2) return BuildErrorAck(pduRef, 0x05);
        int itemCount = param[1];
        var reqItems = ParseItems(param, 2, itemCount);
        int dataStart = 10 + paramLen;
        int dataPos = dataStart;
        var respParams = new List<byte> { 0x05, (byte)itemCount };
        foreach (var it in reqItems)
        {
            int byteLen = it.Transport is 0x01 or 0x03 ? 1 : it.Count * (it.Transport is 0x04 or 0x05 ? 2 : it.Transport is 0x06 or 0x07 or 0x08 ? 4 : 1);
            // 写请求的数据区每个条目带 4 字节头：00 <transportSize> <length(2)>，然后是有效数据
            int payloadStart = dataPos + 4;
            var chunk = (payloadStart + byteLen <= s7.Length) ? s7.AsSpan(payloadStart, byteLen).ToArray() : new byte[byteLen];
            dataPos = payloadStart + byteLen;
            bool ok = WriteItem(it, chunk);
            respParams.Add((byte)(ok ? 0xFF : 0x0A));
        }
        return Ack(pduRef, respParams.ToArray(), Array.Empty<byte>());
    }

    private static byte[] BuildUserData(ushort pduRef, byte[] param)
    {
        // 最小化的用户数据响应（部分客户端请求 SZL，这里返回成功但不含明细）
        var p = param.Length > 0 ? param : new byte[] { 0x07 };
        return Ack(pduRef, p, Array.Empty<byte>());
    }

    private static byte[] BuildErrorAck(ushort pduRef, byte func)
        => Ack(pduRef, new byte[] { (byte)(func | 0x80), 0x00 }, Array.Empty<byte>(), errClass: 0x81);

    private record struct Item(int Transport, int Count, int Db, byte Area, int BitAddress);

    private static List<Item> ParseItems(byte[] param, int offset, int count)
    {
        var list = new List<Item>(count);
        int p = offset;
        for (int i = 0; i < count && p + 9 < param.Length; i++)
        {
            // 12 0A 10 <transportSize> <length(2)> <db(2)> <area(1)> <addr(3)>
            int transport = param[p + 3];
            int len = (param[p + 4] << 8) | param[p + 5];
            int db = (param[p + 6] << 8) | param[p + 7];
            byte area = param[p + 8];
            int addr = (param[p + 9] << 16) | (param[p + 10] << 8) | param[p + 11];
            list.Add(new Item(transport, len, db, area, addr));
            p += 12;
        }
        return list;
    }

    private (bool ok, byte transport, byte[] data) ReadItem(Item it)
    {
        var store = _store!;
        int byteOff = it.BitAddress >> 3;
        int bit = it.BitAddress & 7;
        if (it.Transport == 0x01) // BIT
        {
            if (!GetBits(it.Area, it.Db, byteOff, 1, out var bits)) return (false, 0x03, Array.Empty<byte>());
            return (true, 0x03, new[] { (byte)(bits[0] ? 1 : 0) });
        }
        int size = it.Transport is 0x04 or 0x05 ? 2 : it.Transport is 0x06 or 0x07 or 0x08 ? 4 : 1;
        int bytes = it.Count * size;
        if (!GetBytes(it.Area, it.Db, byteOff, bytes, out var buf)) return (false, 0x04, Array.Empty<byte>());
        return (true, 0x04, buf);
    }

    private bool WriteItem(Item it, byte[] data)
    {
        int byteOff = it.BitAddress >> 3;
        if (it.Transport == 0x01)
            return SetBits(it.Area, it.Db, byteOff, new[] { data.Length > 0 && data[0] != 0 });
        return SetBytes(it.Area, it.Db, byteOff, data);
    }

    // ---------- 数据区读写（映射共享数据区） ----------
    private bool GetBytes(byte area, int db, int offset, int count, out byte[] data)
    {
        data = Array.Empty<byte>();
        var store = _store!;
        lock (store.Sync)
        {
            switch (area)
            {
                case 0x84: // DB
                case 0x83: // M
                {
                    var regs = area == 0x84 ? store.Holding : store.Input;
                    if (offset < 0 || offset + count > regs.Length * 2) return false;
                    data = new byte[count];
                    for (int i = 0; i < count; i++) { int r = (offset + i) / 2; data[i] = ((offset + i) % 2 == 0) ? (byte)(regs[r] >> 8) : (byte)(regs[r] & 0xFF); }
                    return true;
                }
                case 0x81: // I
                case 0x82: // Q
                {
                    var bits = area == 0x81 ? store.Discrete : store.Coils;
                    if (offset < 0 || (offset + count) * 8 > bits.Length) return false;
                    data = new byte[count];
                    for (int b = 0; b < count; b++)
                        for (int k = 0; k < 8; k++)
                            if (bits[(offset + b) * 8 + k]) data[b] |= (byte)(1 << k);
                    return true;
                }
            }
        }
        return false;
    }

    private bool SetBytes(byte area, int db, int offset, byte[] data)
    {
        var store = _store!;
        lock (store.Sync)
        {
            switch (area)
            {
                case 0x84:
                case 0x83:
                {
                    var regs = area == 0x84 ? store.Holding : store.Input;
                    if (offset < 0 || offset + data.Length > regs.Length * 2) return false;
                    for (int i = 0; i < data.Length; i++)
                    {
                        int r = (offset + i) / 2;
                        if ((offset + i) % 2 == 0) regs[r] = (ushort)((data[i] << 8) | (regs[r] & 0x00FF));
                        else regs[r] = (ushort)((regs[r] & 0xFF00) | data[i]);
                    }
                    return true;
                }
                case 0x81:
                case 0x82:
                {
                    var bits = area == 0x81 ? store.Discrete : store.Coils;
                    if (offset < 0 || (offset + data.Length) * 8 > bits.Length) return false;
                    for (int b = 0; b < data.Length; b++)
                        for (int k = 0; k < 8; k++) bits[(offset + b) * 8 + k] = (data[b] & (1 << k)) != 0;
                    return true;
                }
            }
        }
        return false;
    }

    private bool GetBits(byte area, int db, int byteOff, int count, out bool[] bits)
    {
        bits = Array.Empty<bool>();
        if (!GetBytes(area, db, byteOff, count, out var buf)) return false;
        bits = new bool[count];
        for (int i = 0; i < count; i++) bits[i] = (buf[i] & 0x01) != 0;
        return true;
    }

    private bool SetBits(byte area, int db, int byteOff, bool[] bits)
    {
        var buf = new byte[bits.Length];
        for (int i = 0; i < bits.Length; i++) buf[i] = (byte)(bits[i] ? 1 : 0);
        return SetBytes(area, db, byteOff, buf);
    }

    // ---------- 帧封装 ----------
    private static byte[] Ack(ushort pduRef, byte[] param, byte[] data, byte errClass = 0, byte errCode = 0)
    {
        int len = 12 + param.Length + data.Length;
        var b = new byte[len];
        b[0] = 0x32; b[1] = 0x03; b[2] = 0x00; b[3] = 0x00;
        b[4] = (byte)(pduRef >> 8); b[5] = (byte)pduRef;
        b[6] = (byte)(param.Length >> 8); b[7] = (byte)param.Length;
        b[8] = (byte)(data.Length >> 8); b[9] = (byte)data.Length;
        b[10] = errClass; b[11] = errCode;
        Array.Copy(param, 0, b, 12, param.Length);
        Array.Copy(data, 0, b, 12 + param.Length, data.Length);
        return b;
    }

    private static byte[] WrapTpkt(byte[] s7)
    {
        int len = 4 + 3 + s7.Length;
        var b = new byte[len];
        b[0] = 0x03; b[1] = 0x00; b[2] = (byte)(len >> 8); b[3] = (byte)len;
        b[4] = 0x02; b[5] = 0xF0; b[6] = 0x80;   // COTP DT
        Array.Copy(s7, 0, b, 7, s7.Length);
        return b;
    }

    private static async Task<byte[]?> ReadTpktAsync(NetworkStream s, CancellationToken ct)
    {
        var head = new byte[4];
        if (!await ReadExactAsync(s, head, 4, ct)) return null;
        if (head[0] != 0x03) return null;
        int len = (head[2] << 8) | head[3];
        if (len < 4 || len > 0x2000) return null;
        var body = new byte[len - 4];
        if (!await ReadExactAsync(s, body, body.Length, ct)) return null;
        var frame = new byte[len]; Array.Copy(head, frame, 4); Array.Copy(body, 0, frame, 4, body.Length);
        return frame;
    }

    /// <summary>从 TPKT+COTP 帧中取出 S7 PDU（跳过 4 字节 TPKT + COTP 头）。</summary>
    private static byte[]? ExtractS7(byte[] frame)
    {
        if (frame.Length < 7) return null;
        int cotpLen = frame[4];           // COTP 头长度（不含自身）
        int off = 4 + 1 + cotpLen;        // TPKT(4) + COTP len 字节 + COTP 内容
        if (off >= frame.Length) return null;
        return frame.AsSpan(off).ToArray();
    }

    private static async Task<bool> ReadExactAsync(NetworkStream s, byte[] buf, int count, CancellationToken ct)
    {
        int off = 0;
        while (off < count)
        {
            int n;
            try { n = await s.ReadAsync(buf.AsMemory(off, count - off), ct); }
            catch { return false; }
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }
}
