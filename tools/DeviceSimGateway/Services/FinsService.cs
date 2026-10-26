namespace DeviceSimGateway.Services;

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DeviceSimGateway.Core;

/// <summary>Omron FINS/TCP 服务端：FINS 头(魔术字 FINS + 长度 + 命令 + 错误码) + FINS 帧。
/// 支持节点地址交换与内存区读写（命令 0101/0102；区 CIO/WR/HR/AR/DM，字/位）。</summary>
public sealed class FinsService : IDeviceService
{
    public string Key => "fins";
    public string Name => "Omron FINS";
    public string Description => "FINS/TCP 服务端（0101/0102，CIO/WR/HR/AR/DM）";
    public int DefaultPort => 9600;
    public bool Implemented => true;
    public ServiceState State { get; private set; } = ServiceState.Stopped;
    public string Message { get; private set; } = "";
    public int Port { get; private set; }

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
        Message = $"监听 0.0.0.0:{port}（FINS/TCP）";
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
            var header = new byte[16];
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactAsync(s, header, 16, ct)) break;
                if (!(header[0] == (byte)'F' && header[1] == (byte)'I' && header[2] == (byte)'N' && header[3] == (byte)'S')) break;
                int len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4));
                int command = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4));
                if (len < 0 || len > 0x20000) break;
                var payload = new byte[Math.Max(0, len - 8)]; // 命令/错误字段后为负载（长度含 command+error 共 8 字节）
                // 实际：长度字段=其后总字节数(含 command 4 + error 4 + payload)
                if (payload.Length > 0 && !await ReadExactAsync(s, payload, payload.Length, ct)) break;

                byte[] respPayload;
                if (command == 0 && payload.Length <= 4)
                {
                    // 节点地址交换：返回本机节点地址（4 字节）
                    respPayload = new byte[] { 0, 0, 0, 1 };
                }
                else
                {
                    respPayload = HandleFins(payload);
                }
                await WriteResponseAsync(s, command, respPayload, ct);
            }
        }
    }

    private static async Task WriteResponseAsync(NetworkStream s, int command, byte[] payload, CancellationToken ct)
    {
        int len = 8 + payload.Length;
        var buf = new byte[8 + len];
        buf[0] = (byte)'F'; buf[1] = (byte)'I'; buf[2] = (byte)'N'; buf[3] = (byte)'S';
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(4, 4), len);
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(8, 4), command);
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(12, 4), 0);   // error code = 0
        Array.Copy(payload, 0, buf, 16, payload.Length);
        await s.WriteAsync(buf, ct); await s.FlushAsync(ct);
    }

    /// <summary>处理 FINS 帧（自 ICF 起）。</summary>
    private byte[] HandleFins(byte[] f)
    {
        if (f.Length < 12) return Array.Empty<byte>();
        // FINS: ICF RSV GCT DNA DA1 DA2 SNA SA1 SA2 SID | MRC SRC | data...
        byte icf = f[0], sid = f[9], mrc = f[10], src = f[11];
        var body = f.AsSpan(12).ToArray();
        var resp = new List<byte> { icf, 0, f[2], 0, 0, 0, 0, f[7], 0, sid, mrc, src };
        try
        {
            if (mrc == 0x01 && src == 0x01) return ReadMemory(resp, body);
            if (mrc == 0x01 && src == 0x02) return WriteMemory(resp, body);
            resp.Add(0x01); resp.Add(0x40); // unsupported command
            return resp.ToArray();
        }
        catch { resp.Add(0x01); resp.Add(0x05); return resp.ToArray(); } // data error
    }

    private byte[] ReadMemory(List<byte> resp, byte[] body)
    {
        // body: area(1) addr(2) bit(1) count(2)
        if (body.Length < 6) { resp.Add(0x01); resp.Add(0x05); return resp.ToArray(); }
        byte area = body[0];
        int addr = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(1, 2));
        byte bit = body[3];
        int count = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(4, 2));
        var store = _store!;
        lock (store.Sync)
        {
            if (TryArea(area, out var regs, out var isBit))
            {
                if (isBit || bit > 0)
                {
                    var bits = new bool[count];
                    for (int i = 0; i < count; i++) bits[i] = (addr + i) < regs.Length * 16 && GetBit(regs, addr + i);
                    resp.Add(0x00); resp.Add(0x00);
                    for (int i = 0; i < count; i++) resp.Add((byte)(bits[i] ? 1 : 0));
                    return resp.ToArray();
                }
                resp.Add(0x00); resp.Add(0x00);
                for (int i = 0; i < count; i++)
                {
                    ushort v = (addr + i) < regs.Length ? regs[addr + i] : (ushort)0;
                    resp.Add((byte)(v >> 8)); resp.Add((byte)(v & 0xFF));
                }
                return resp.ToArray();
            }
        }
        resp.Add(0x01); resp.Add(0x03); // area error
        return resp.ToArray();
    }

    private byte[] WriteMemory(List<byte> resp, byte[] body)
    {
        if (body.Length < 6) { resp.Add(0x01); resp.Add(0x05); return resp.ToArray(); }
        byte area = body[0];
        int addr = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(1, 2));
        byte bit = body[3];
        int count = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(4, 2));
        var store = _store!;
        int dataLen = body.Length - 6;
        lock (store.Sync)
        {
            if (TryArea(area, out var regs, out var isBit))
            {
                if (isBit || bit > 0)
                {
                    for (int i = 0; i < count && i < dataLen; i++) SetBit(regs, addr + i, body[6 + i] != 0);
                }
                else
                {
                    for (int i = 0; i < count && 6 + i * 2 + 1 < body.Length; i++)
                    {
                        ushort v = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(6 + i * 2, 2));
                        if (addr + i < regs.Length) regs[addr + i] = v;
                    }
                }
                resp.Add(0x00); resp.Add(0x00);
                return resp.ToArray();
            }
        }
        resp.Add(0x01); resp.Add(0x03);
        return resp.ToArray();
    }

    private (ushort[] regs, bool isBit)? AreaMap(byte area, SimDataStore s) => area switch
    {
        0x82 or 0x02 => (s.Holding, area == 0x02),   // DM word / DM bit
        0xB0 or 0x30 => (s.Input, area == 0x30),     // CIO word / CIO bit
        0xB1 or 0x31 => (s.Input, area == 0x31),     // WR
        0xB2 or 0x32 => (s.Input, area == 0x32),     // HR
        0xB3 or 0x33 => (s.Input, area == 0x33),     // AR
        _ => null
    };

    private bool TryArea(byte area, out ushort[] regs, out bool isBit)
    {
        var m = AreaMap(area, _store!);
        if (m is null) { regs = Array.Empty<ushort>(); isBit = false; return false; }
        regs = m.Value.regs; isBit = m.Value.isBit; return true;
    }

    private static bool GetBit(ushort[] regs, int bitIndex)
    {
        int w = bitIndex / 16, b = bitIndex % 16;
        return w < regs.Length && (regs[w] & (1 << b)) != 0;
    }

    private static void SetBit(ushort[] regs, int bitIndex, bool value)
    {
        int w = bitIndex / 16, b = bitIndex % 16;
        if (w >= regs.Length) return;
        if (value) regs[w] |= (ushort)(1 << b); else regs[w] &= (ushort)~(1 << b);
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
