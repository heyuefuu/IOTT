using System.Runtime.InteropServices;

// FOCAS2 直连探测（P/Invoke Fwlib64.dll）：判断某 IP:Port 能否建立 FOCAS2/Ethernet 会话，
// 并打印 CNC 类型/系列、运行状态、绝对坐标。用于现场"真机协议配置"判定。
// 用法: dotnet FocasProbe.dll <ip> [port] [timeoutSec]

const string Dll = "Fwlib64.dll";

[DllImport(Dll)] static extern short cnc_allclibhndl3([MarshalAs(UnmanagedType.LPStr)] string ip, ushort port, int timeout, out ushort handle);
[DllImport(Dll)] static extern short cnc_freelibhndl(ushort handle);
[DllImport(Dll)] static extern short cnc_sysinfo(ushort handle, [Out] ODBSYS sys);
[DllImport(Dll)] static extern short cnc_statinfo(ushort handle, [Out] ODBST st);
[DllImport(Dll)] static extern short cnc_absolute(ushort handle, short axis, short length, [Out] ODBAXIS pos);
[DllImport(Dll)] static extern short cnc_rdparam(ushort handle, short number, short axis, short length, [Out] IODBPSD_1 param);
[DllImport(Dll)] static extern short cnc_rdmacro(ushort handle, short number, short length, [Out] ODBM macro);

var ip = args.Length > 0 ? args[0] : "192.168.1.1";
var port = args.Length > 1 ? ushort.Parse(args[1]) : (ushort)8193;
var timeout = args.Length > 2 ? int.Parse(args[2]) : 10;

Console.WriteLine($"[FocasProbe] target {ip}:{port}  timeout={timeout}s   now={DateTime.Now:O}");
Console.WriteLine($"[FocasProbe] lib {Dll}  dir={AppContext.BaseDirectory}");

var rc = cnc_allclibhndl3(ip, port, timeout, out var h);
Console.WriteLine($"cnc_allclibhndl3 -> rc={rc} {Name(rc)}  handle={h}");
if (rc != 0) { Console.WriteLine("[FocasProbe] CONNECT FAILED"); return; }

try
{
    var sys = new ODBSYS();
    var rc2 = cnc_sysinfo(h, sys);
    Console.WriteLine($"cnc_sysinfo -> rc={rc2} {Name(rc2)}");
    if (rc2 == 0)
        Console.WriteLine($"   cnc_type='{S(sys.cnc_type)}'  mt_type='{S(sys.mt_type)}'  series='{S(sys.series)}'  version='{S(sys.version)}'  axes='{S(sys.axes)}'  max_axis={sys.max_axis}");

    var st = new ODBST();
    var rc3 = cnc_statinfo(h, st);
    Console.WriteLine($"cnc_statinfo -> rc={rc3} {Name(rc3)}");
    if (rc3 == 0)
        Console.WriteLine($"   aut(模式)={st.aut}  run={st.run}  motion={st.motion}  alarm={st.alarm}  emg={st.emergency}  edit={st.edit}");

    for (short axis = 1; axis <= 3; axis++)
    {
        var pos = new ODBAXIS();
        var rc4 = cnc_absolute(h, axis, 8, pos);
        Console.WriteLine($"cnc_absolute(axis={axis}) -> rc={rc4} {Name(rc4)}  type={pos.type} data[0]={pos.data[0]}");
    }

    // 按现场可用样例 WindowsFormsAppforFanuc 的参数读法（含 2 字长 8+32）验证：
    Console.WriteLine("-- 参数/宏（对齐样例 WindowsFormsAppforFanuc）--");
    foreach (var (num, len, note) in new (short num, short len, string note)[]
    {
        (6712, 8, "总加工数"),
        (6750, 8 + 32, "开机时间(分)"),
        (6751, 8, "运行时间(秒/1000)"),
        (6752, 8, "运行时间(分)"),
        (6753, 8 + 32, "切削时间(秒/1000)"),
        (6754, 8 + 32, "切削时间(分)"),
    })
    {
        var p = new IODBPSD_1();
        var rcp = cnc_rdparam(h, num, 0, len, p);
        Console.WriteLine($"cnc_rdparam({num}, len={len}, {note}) -> rc={rcp} {Name(rcp)}  datano={p.datano} type={p.type} ldata={p.ldata}");
    }
    var m = new ODBM();
    var rcm = cnc_rdmacro(h, 3901, 10, m);
    Console.WriteLine($"cnc_rdmacro(3901) -> rc={rcm} {Name(rcm)}  datano={m.datano} mcr_val={m.mcr_val} dec_val={m.dec_val}  -> /100000={m.mcr_val / 100000.0}  *10^-dec={(m.dec_val > 0 ? m.mcr_val * Math.Pow(10, -m.dec_val) : m.mcr_val)}");

    Console.WriteLine("-- 对照：同一参数用 length=8 读（旧实现）--");
    foreach (short num in new short[] { 6750, 6753, 6754 })
    {
        var p8 = new IODBPSD_1();
        var rc8 = cnc_rdparam(h, num, 0, 8, p8);
        Console.WriteLine($"cnc_rdparam({num}, len=8 ) -> rc={rc8} {Name(rc8)}  ldata={p8.ldata}");
    }

    Console.WriteLine("[FocasProbe] CONNECT OK");
}
finally
{
    var rc5 = cnc_freelibhndl(h);
    Console.WriteLine($"cnc_freelibhndl -> rc={rc5} {Name(rc5)}");
}

static string S(char[] a) => new string(a).Trim('\0', ' ');
static string Name(int code) => code switch
{
    -17 => "EW_NODLL", -16 => "EW_BUS", -15 => "EW_HSSB", -11 => "EW_MMCSYS", -10 => "EW_SYSTEM2",
    -9 => "EW_VERSION", -8 => "EW_HANDLE", -7 => "EW_UNEXP", -6 => "EW_SYSTEM", -5 => "EW_PARITY",
    -4 => "EW_OVERRUN", -3 => "EW_FRAMING", -2 => "EW_RESET", -1 => "EW_BUSY",
    0 => "EW_OK", 1 => "EW_FUNC", 2 => "EW_LENGTH", 3 => "EW_NUMBER", 4 => "EW_RANGE", 5 => "EW_DATA",
    6 => "EW_NOOPT", 7 => "EW_PROT", 8 => "EW_OVRFLOW", 9 => "EW_PARAM", 10 => "EW_BUFFER", 11 => "EW_PATH",
    12 => "EW_MODE", 13 => "EW_REJECT", 14 => "EW_DTSRVR", 15 => "EW_ALARM", 16 => "EW_STOP", 17 => "EW_PASSWD",
    _ => ""
};

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public class ODBSYS
{
    public short addinfo;
    public short max_axis;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public char[] cnc_type = new char[2];
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public char[] mt_type = new char[2];
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public char[] series = new char[4];
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public char[] version = new char[4];
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public char[] axes = new char[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public class ODBST
{
    public short hdck; public short tmmode; public short aut; public short run; public short motion;
    public short mstb; public short emergency; public short alarm; public short edit;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public class ODBAXIS
{
    public short dummy;
    public short type;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] data = new int[8];
}

[StructLayout(LayoutKind.Explicit, Pack = 4)]
public class IODBPSD_1
{
    [FieldOffset(0)] public short datano;
    [FieldOffset(2)] public short type;
    [FieldOffset(4)] public byte cdata;
    [FieldOffset(4)] public short idata;
    [FieldOffset(4)] public int ldata;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public class ODBM
{
    public short datano;
    public short dummy;
    public int mcr_val;
    public short dec_val;
}
