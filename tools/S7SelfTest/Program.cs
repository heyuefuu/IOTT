// 用独立客户端库 S7.Net 验证 DeviceSimGateway 的 S7 服务端是否符合 S7comm。
// 用法： dotnet run --project tools/S7SelfTest -- [host] [port]
using S7.Net;

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 ? int.Parse(args[1]) : 102;

try
{
    using var plc = new Plc(CpuType.S71500, host, 0, 1);
    plc.Open();
    Console.WriteLine($"[OK] 已连接 {host}:{port}  PDU={plc.MaxPDUSize}");

    var before = plc.ReadBytes(DataType.DataBlock, 1, 0, 8);
    Console.WriteLine("DB1[0..7] 读 = " + BitConverter.ToString(before));

    // 写入 DB1.2 = 0x1234，再读回
    plc.WriteBytes(DataType.DataBlock, 1, 2, new byte[] { 0x12, 0x34 });
    var after = plc.ReadBytes(DataType.DataBlock, 1, 0, 8);
    Console.WriteLine("写 DB1.2=1234 后读回 = " + BitConverter.ToString(after));

    // 读一个字（DBW0）
    var word = plc.Read("DB1.DBW0");
    Console.WriteLine("DB1.DBW0 = " + word);

    Console.WriteLine("[PASS] S7 服务端互通正常");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine("[FAIL] " + ex.GetType().Name + ": " + ex.Message);
    return 1;
}
