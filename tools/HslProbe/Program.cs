// 探针/回归：
//   listen <port> [replyHex]     监听并打印收到的原始字节（可选固定应答）
//   mewtocol <host> <port>       用 HSL PanasonicMewtocolOverTcp 客户端读/写
//   fins <host> <port>           用 HSL OmronFinsNet 客户端读/写
using System.Net;
using System.Net.Sockets;
using System.Text;
using HslCommunication.Profinet.Omron;
using HslCommunication.Profinet.Panasonic;

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "listen";

if (mode == "listen")
{
    int port = args.Length > 1 ? int.Parse(args[1]) : 5007;
    byte[]? reply = args.Length > 2 ? Convert.FromHexString(args[2].Replace(" ", "")) : null;
    var listener = new TcpListener(IPAddress.Any, port);
    listener.Start();
    Console.WriteLine($"listening on {port}  (reply={(reply is null ? "none" : Convert.ToHexString(reply))})");
    while (true)
    {
        using var c = await listener.AcceptTcpClientAsync();
        Console.WriteLine("client connected");
        var s = c.GetStream();
        var buf = new byte[4096];
        try
        {
            while (true)
            {
                int n = await s.ReadAsync(buf);
                if (n <= 0) break;
                var data = buf.AsSpan(0, n).ToArray();
                Console.WriteLine($"RX({n}) HEX={Convert.ToHexString(data)}  ASCII={Encoding.ASCII.GetString(data).Replace("\r", "<CR>").Replace("\n", "<LF>")}");
                if (reply is not null) { await s.WriteAsync(reply); await s.FlushAsync(); Console.WriteLine($"TX({reply.Length}) HEX={Convert.ToHexString(reply)}"); }
            }
        }
        catch (Exception ex) { Console.WriteLine("err: " + ex.Message); }
        Console.WriteLine("client disconnected");
    }
}
else if (mode == "mewtocol")
{
    var host = args.Length > 1 ? args[1] : "127.0.0.1";
    int port = args.Length > 2 ? int.Parse(args[2]) : 5007;
    var plc = new PanasonicMewtocolOverTcp(host, port) { Station = 1 };
    var conn = plc.ConnectServer();
    Console.WriteLine("connect: " + conn.IsSuccess + " " + conn.Message);
    foreach (var addr in new[] { "D100", "D0", "R0", "X0", "Y0" })
    {
        var r = plc.ReadInt16(addr);
        Console.WriteLine($"ReadInt16({addr}) -> ok={r.IsSuccess} err={r.Message} value={r.Content}");
    }
    var w = plc.Write("D100", (short)1234);
    Console.WriteLine("Write(D100,1234) -> " + w.IsSuccess + " " + w.Message);
    var r2 = plc.ReadInt16("D100");
    Console.WriteLine("ReadInt16(D100) -> " + r2.Content);
    plc.ConnectClose();
}
else if (mode == "fins")
{
    var host = args.Length > 1 ? args[1] : "127.0.0.1";
    int port = args.Length > 2 ? int.Parse(args[2]) : 9600;
    var plc = new OmronFinsNet(host, port);
    var conn = plc.ConnectServer();
    Console.WriteLine("connect: " + conn.IsSuccess + " " + conn.Message);
    foreach (var addr in new[] { "D100", "D0", "W0", "H0" })
    {
        var r = plc.ReadInt16(addr);
        Console.WriteLine($"ReadInt16({addr}) -> ok={r.IsSuccess} err={r.Message} value={r.Content}");
    }
    var w = plc.Write("D100", (short)4321);
    Console.WriteLine("Write(D100,4321) -> " + w.IsSuccess + " " + w.Message);
    var r2 = plc.ReadInt16("D100");
    Console.WriteLine("ReadInt16(D100) -> " + r2.Content);
    plc.ConnectClose();
}
else Console.WriteLine("unknown mode");
