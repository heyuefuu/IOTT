namespace DeviceSimGateway.Services;

using System.Net;
using System.Net.Sockets;
using DeviceSimGateway.Core;

/// <summary>严格按 Modbus/TCP 实现（MBAP 头 + 功能码 01/02/03/04/05/06/0F/10，含标准异常码）。</summary>
public sealed class ModbusTcpService : IDeviceService
{
    public string Key => "modbus";
    public string Name => "Modbus TCP";
    public string Description => "标准 Modbus/TCP 从站（FC 01/02/03/04/05/06/0F/10）";
    public int DefaultPort => 502;
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
        _store = data;
        Port = port;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        State = ServiceState.Running;
        Message = $"监听 0.0.0.0:{port}";
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
            var stream = client.GetStream();
            var header = new byte[6];
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, header, 6, ct)) break;
                int len = (header[4] << 8) | header[5];
                if (len < 2 || len > 260) break; // unitId + PDU，PDU 最大 253
                var body = new byte[len];
                if (!await ReadExactAsync(stream, body, len, ct)) break;

                byte unitId = body[0];
                var pdu = body.AsSpan(1).ToArray();
                var fail = _store!.Failure.ShouldFail();
                if (fail && _store.Failure.DropConnection) break;   // 随机失败：断链
                var respPdu = fail ? Exception(pdu.Length > 0 ? pdu[0] : (byte)0x03, 0x04) : BuildResponse(pdu);
                var resp = new byte[7 + respPdu.Length];
                resp[0] = header[0]; resp[1] = header[1];       // transactionId 原样回
                resp[2] = 0; resp[3] = 0;                        // protocolId = 0
                int respLen = 1 + respPdu.Length;
                resp[4] = (byte)(respLen >> 8); resp[5] = (byte)(respLen & 0xFF);
                resp[6] = unitId;
                Array.Copy(respPdu, 0, resp, 7, respPdu.Length);
                try { await stream.WriteAsync(resp, ct); await stream.FlushAsync(ct); }
                catch { break; }
            }
        }
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

    private static ushort U16(ReadOnlySpan<byte> b, int o) => (ushort)((b[o] << 8) | b[o + 1]);

    /// <summary>根据 PDU（function + data）生成响应 PDU；异常返回 [fc|0x80, code]。</summary>
    private byte[] BuildResponse(byte[] pdu)
    {
        if (pdu.Length < 1) return new byte[] { 0x80, 0x03 };
        byte fc = pdu[0];
        var store = _store!;
        try
        {
            switch (fc)
            {
                case 0x01: // Read Coils
                case 0x02: // Read Discrete Inputs
                {
                    if (pdu.Length < 5) return Exception(fc, 0x03);
                    int start = U16(pdu, 1); int qty = U16(pdu, 3);
                    if (qty < 1 || qty > 2000) return Exception(fc, 0x03);
                    if (!store.ReadBits(fc == 0x01, start, qty, out var bits)) return Exception(fc, 0x02);
                    int byteCount = (qty + 7) / 8;
                    var resp = new byte[2 + byteCount];
                    resp[0] = fc; resp[1] = (byte)byteCount;
                    for (int i = 0; i < qty; i++) if (bits[i]) resp[2 + (i / 8)] |= (byte)(1 << (i % 8));
                    return resp;
                }
                case 0x03: // Read Holding Registers
                case 0x04: // Read Input Registers
                {
                    if (pdu.Length < 5) return Exception(fc, 0x03);
                    int start = U16(pdu, 1); int qty = U16(pdu, 3);
                    if (qty < 1 || qty > 125) return Exception(fc, 0x03);
                    if (!store.ReadRegisters(fc == 0x03, start, qty, out var regs)) return Exception(fc, 0x02);
                    var resp = new byte[2 + qty * 2];
                    resp[0] = fc; resp[1] = (byte)(qty * 2);
                    for (int i = 0; i < qty; i++) { resp[2 + i * 2] = (byte)(regs[i] >> 8); resp[3 + i * 2] = (byte)(regs[i] & 0xFF); }
                    return resp;
                }
                case 0x05: // Write Single Coil
                {
                    if (pdu.Length < 5) return Exception(fc, 0x03);
                    int addr = U16(pdu, 1); int val = U16(pdu, 3);
                    if (val != 0x0000 && val != 0xFF00) return Exception(fc, 0x03);
                    if (!store.WriteCoil(addr, val == 0xFF00)) return Exception(fc, 0x02);
                    return [fc, pdu[1], pdu[2], pdu[3], pdu[4]];
                }
                case 0x06: // Write Single Register
                {
                    if (pdu.Length < 5) return Exception(fc, 0x03);
                    int addr = U16(pdu, 1); ushort val = U16(pdu, 3);
                    if (!store.WriteRegister(addr, val)) return Exception(fc, 0x02);
                    return [fc, pdu[1], pdu[2], pdu[3], pdu[4]];
                }
                case 0x0F: // Write Multiple Coils
                {
                    if (pdu.Length < 6) return Exception(fc, 0x03);
                    int start = U16(pdu, 1); int qty = U16(pdu, 3); int byteCount = pdu[5];
                    if (qty < 1 || qty > 1968 || byteCount != (qty + 7) / 8 || pdu.Length < 6 + byteCount) return Exception(fc, 0x03);
                    var bits = new bool[qty];
                    for (int i = 0; i < qty; i++) bits[i] = (pdu[6 + (i / 8)] & (1 << (i % 8))) != 0;
                    if (!store.WriteCoils(start, bits)) return Exception(fc, 0x02);
                    return [fc, pdu[1], pdu[2], pdu[3], pdu[4]];
                }
                case 0x10: // Write Multiple Registers
                {
                    if (pdu.Length < 6) return Exception(fc, 0x03);
                    int start = U16(pdu, 1); int qty = U16(pdu, 3); int byteCount = pdu[5];
                    if (qty < 1 || qty > 123 || byteCount != qty * 2 || pdu.Length < 6 + byteCount) return Exception(fc, 0x03);
                    var regs = new ushort[qty];
                    for (int i = 0; i < qty; i++) regs[i] = U16(pdu, 6 + i * 2);
                    if (!store.WriteRegisters(start, regs)) return Exception(fc, 0x02);
                    return [fc, pdu[1], pdu[2], pdu[3], pdu[4]];
                }
                default:
                    return Exception(fc, 0x01); // Illegal Function
            }
        }
        catch { return Exception(fc, 0x04); } // Server Device Failure
    }

    private static byte[] Exception(byte fc, byte code) => [(byte)(fc | 0x80), code];
}
