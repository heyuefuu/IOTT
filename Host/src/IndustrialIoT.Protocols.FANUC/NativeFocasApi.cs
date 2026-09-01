namespace IndustrialIoT.Protocols.FANUC;

using System.Runtime.InteropServices;
using IndustrialIoT.Protocols.Models;

/// <summary>
/// Real FOCAS2 API implementation using FANUC native DLLs (Fwlib64.dll).
/// This wraps the official FANUC FOCAS2 Library V4.7 P/Invoke calls.
/// Requires Fwlib64.dll + fwlib0iD64.dll + fwlibe64.dll in the output directory.
///
/// Supported CNC: FANUC 0i-MF Plus, 0i-D, 30i/31i/32i, etc.
/// </summary>
public sealed class NativeFocasApi : IFocasApi
{
    private const short EW_OK = 0;
    private const short EW_NUMBER = 3;
    public bool SupportsProgramBlockRead => false;
    public bool SupportsModalReads => false;

    #region ── Native FOCAS2 DllImport ──────────────────────────────────

    // Connection
    [DllImport("Fwlib64.dll", EntryPoint = "cnc_allclibhndl3")]
    private static extern short cnc_allclibhndl3(
        [MarshalAs(UnmanagedType.LPStr)] string ip,
        ushort port, int timeout, out ushort flibHndl);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_freelibhndl")]
    private static extern short cnc_freelibhndl(ushort flibHndl);

    // Axis position
    [DllImport("Fwlib64.dll", EntryPoint = "cnc_absolute")]
    private static extern short cnc_absolute(ushort flibHndl, short axis, short length, [Out] ODBAXIS pos);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_machine")]
    private static extern short cnc_machine(ushort flibHndl, short axis, short length, [Out] ODBAXIS pos);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_rdposition")]
    private static extern short cnc_rdposition(ushort flibHndl, short type, ref short axisCount, [Out] ODBPOS pos);

    // Spindle
    [DllImport("Fwlib64.dll", EntryPoint = "cnc_actf")]
    private static extern short cnc_actf(ushort flibHndl, [Out] ODBACT act);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_acts")]
    private static extern short cnc_acts(ushort flibHndl, [Out] ODBACT act);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_rdspeed")]
    private static extern short cnc_rdspeed(ushort flibHndl, short type, [Out] ODBSPEED speed);

    // Status
    [DllImport("Fwlib64.dll", EntryPoint = "cnc_statinfo")]
    private static extern short cnc_statinfo(ushort flibHndl, [Out] ODBST stat);

    // Program number
    [DllImport("Fwlib64.dll", EntryPoint = "cnc_sysinfo")]
    private static extern short cnc_sysinfo(ushort flibHndl, [Out] ODBSYS sys);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_rdprgnum")]
    private static extern short cnc_rdprgnum(ushort flibHndl, [Out] ODBPRO prog);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_exeprgname")]
    private static extern short cnc_exeprgname(ushort flibHndl, [Out] ODBEXEPRG program);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_rdexecprog")]
    private static extern short cnc_rdexecprog(ushort flibHndl, ref ushort length, [Out] byte[] blkbuf);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_modal")]
    private static extern short cnc_modal(ushort flibHndl, short type, short block, short length, [Out] ODBMDL modal);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_dwnstart")]
    private static extern short cnc_dwnstart(ushort flibHndl);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_dwnstart4")]
    private static extern short cnc_dwnstart4(ushort flibHndl, short type, [MarshalAs(UnmanagedType.LPStr)] string dirName);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_download")]
    private static extern short cnc_download(ushort flibHndl, byte[] data, short number);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_download4")]
    private static extern short cnc_download4(ushort flibHndl, ref int number, [In, MarshalAs(UnmanagedType.AsAny)] object data);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_dwnend")]
    private static extern short cnc_dwnend(ushort flibHndl);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_dwnend4")]
    private static extern short cnc_dwnend4(ushort flibHndl);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_upstart")]
    private static extern short cnc_upstart(ushort flibHndl, short number);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_upstart4")]
    private static extern short cnc_upstart4(ushort flibHndl, short type, [Out, MarshalAs(UnmanagedType.AsAny)] object fileName);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_upload")]
    private static extern short cnc_upload(ushort flibHndl, [Out] ODBUP upload, ref ushort number);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_upload4")]
    private static extern short cnc_upload4(ushort flibHndl, ref int length, [Out, MarshalAs(UnmanagedType.AsAny)] object data);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_upend")]
    private static extern short cnc_upend(ushort flibHndl);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_upend4")]
    private static extern short cnc_upend4(ushort flibHndl);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_rdprogdir3")]
    private static extern short cnc_rdprogdir3(ushort flibHndl, short type,
        ref int topNumber, ref short numProg, [Out] PRGDIR3[] buf);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_rdpdf_curdir")]
    private static extern short cnc_rdpdf_curdir(ushort flibHndl, short type, [Out, MarshalAs(UnmanagedType.AsAny)] object path);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_rdpdf_subdir")]
    private static extern short cnc_rdpdf_subdir(ushort flibHndl, ref short number, [In] IDBPDFSDIR request, [Out] ODBPDFSDIR entry);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_rdpdf_alldir")]
    private static extern short cnc_rdpdf_alldir(ushort flibHndl, ref short number, [In, MarshalAs(UnmanagedType.AsAny)] object request, [Out, MarshalAs(UnmanagedType.AsAny)] object entry);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_rdpdf_subdirn")]
    private static extern short cnc_rdpdf_subdirn(ushort flibHndl, [In, MarshalAs(UnmanagedType.AsAny)] object path, [Out] ODBPDFNFIL count);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_getdtailerr")]
    private static extern short cnc_getdtailerr(ushort flibHndl, [Out, MarshalAs(UnmanagedType.LPStruct)] ODBERR error);

    // Alarm
    [DllImport("Fwlib64.dll", EntryPoint = "cnc_alarm")]
    private static extern short cnc_alarm(ushort flibHndl, [Out] ODBALM alarm);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_rdalmmsg")]
    private static extern short cnc_rdalmmsg(ushort flibHndl, short type, ref short num, [Out] ODBALMMSG[] almmsg);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_alarm2")]
    private static extern short cnc_alarm2(ushort flibHndl, out int alarmStatus);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_rdmaxgrp")]
    private static extern short cnc_rdmaxgrp(ushort flibHndl, [Out] ODBLFNO maxGroup);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_rdmacro")]
    private static extern short cnc_rdmacro(ushort flibHndl, short number, short length, [Out] ODBM macro);

    [DllImport("Fwlib64.dll", EntryPoint = "cnc_rdparam")]
    private static extern short cnc_rdparam(ushort flibHndl, short number, short axis, short length, [Out] IODBPSD_1 param);

    // PMC (I/O)
    [DllImport("Fwlib64.dll", EntryPoint = "pmc_rdpmcrng")]
    private static extern short pmc_rdpmcrng(ushort flibHndl, short adrType, short dataType,
        ushort startNo, ushort endNo, ushort length, [Out] IODBPMC pmcData);

    [DllImport("Fwlib64.dll", EntryPoint = "pmc_wrpmcrng")]
    private static extern short pmc_wrpmcrng(ushort flibHndl, ushort length, [In] IODBPMC pmcData);

    #endregion

    #region ── Native Structures ────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class ODBAXIS
    {
        public short dummy;
        public short type;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public int[] data = new int[8];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class POSELM
    {
        public int data;
        public short dec;
        public short unit;
        public short disp;
        public char name;
        public char suff;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class POSELMALL
    {
        public POSELM abs = new();
        public POSELM mach = new();
        public POSELM rel = new();
        public POSELM dist = new();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class ODBACT
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public short[] dummy = new short[2];
        public int data;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class ODBSPEED
    {
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct SPEEDELM
        {
            public int data;
            public short dec;
            public short unit;
            public short reserve;
            public char name;
            public char suff;
        }
        public SPEEDELM actf;
        public SPEEDELM acts;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class ODBST
    {
        public short hdck;     // handwheel check
        public short tmmode;   // T/M mode
        public short aut;      // selected automatic mode (0=MDI,1=MEM,3=EDIT,4=HANDLE,5=JOG,6=REF)
        public short run;      // running status (0=RESET,1=STOP,2=HOLD,3=START,4=MSTR)
        public short motion;   // axis motion status (0=***,1=motion,2=dwell,3=wait)
        public short mstb;     // M/S/T/B status
        public short emergency;// emergency stop status
        public short alarm;    // alarm status
        public short edit;     // edit status
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class ODBPRO
    {
        public short dummy1;
        public short dummy2;
        public short data;     // running program number
        public short mdata;    // main program number
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class ODBSYS
    {
        public short addinfo;
        public short max_axis;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public char[] cnc_type = new char[2];
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public char[] mt_type = new char[2];
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public char[] series = new char[4];
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public char[] version = new char[4];
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public char[] axes = new char[2];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class ODBEXEPRG
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)]
        public char[] name = new char[36];
        public int o_num;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class ODBUP
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public short[] dummy = new short[2];
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] data = new byte[256];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class ODBPOS
    {
        public POSELMALL p1 = new();
        public POSELMALL p2 = new();
        public POSELMALL p3 = new();
        public POSELMALL p4 = new();
        public POSELMALL p5 = new();
        public POSELMALL p6 = new();
        public POSELMALL p7 = new();
        public POSELMALL p8 = new();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class ODBMDL
    {
        public short datano;
        public short type;
        public int aux_data;
        public short flag1;
        public short flag2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class ODBALM
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public short[] dummy = new short[2];
        public short data;     // alarm type
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class ODBALMMSG
    {
        public int alm_no;
        public short type;
        public short axis;
        public short dummy;
        public short msg_len;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string alm_msg = "";
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class ODBLFNO
    {
        public short datano;
        public short type;
        public short data;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class ODBM
    {
        public short datano;
        public short dummy;
        public int mcr_val;
        public short dec_val;
    }

    [StructLayout(LayoutKind.Explicit, Pack = 4)]
    private class IODBPSD_1
    {
        [FieldOffset(0)]
        public short datano;
        [FieldOffset(2)]
        public short type;
        [FieldOffset(4)]
        public byte cdata;
        [FieldOffset(4)]
        public short idata;
        [FieldOffset(4)]
        public int ldata;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class IODBPMC
    {
        public short type_a;  // PMC address type
        public short type_d;  // PMC data type (0=byte)
        public ushort datano_s; // start PMC address
        public ushort datano_e; // end PMC address
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] cdata = new byte[256];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PRGDIR3_DATE
    {
        public short year;
        public short month;
        public short day;
        public short hour;
        public short minute;
        public short dummy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
    private struct PRGDIR3
    {
        public int number;
        public int length;
        public int page;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 52)]
        public string comment;
        public PRGDIR3_DATE mdate;
        public PRGDIR3_DATE cdate;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
    private class IDBPDFSDIR
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 212)]
        public string path = new(' ', 212);
        public short req_num;
        public short dummy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
    private class ODBPDFSDIR
    {
        public short sub_exist;
        public short dummy;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 36)]
        public string d_f = new(' ', 36);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
    private class IDBPDFADIR
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 212)]
        public string path = new(' ', 212);
        public short req_num;
        public short size_kind;
        public short type;
        public short dummy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
    private class ODBPDFADIR
    {
        public short data_kind;
        public short year;
        public short mon;
        public short day;
        public short hour;
        public short min;
        public short sec;
        public short dummy;
        public int dummy2;
        public int size;
        public uint attr;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 36)]
        public string d_f = new(' ', 36);
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 52)]
        public string comment = new(' ', 52);
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 12)]
        public string o_time = new(' ', 12);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class ODBPDFNFIL
    {
        public short dir_num;
        public short file_num;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private class ODBERR
    {
        public short err_no;
        public short err_dtno;
    }

    #endregion

    #region ── IFocasApi Implementation ─────────────────────────────────

    public int Connect(string host, int port, int timeout, out int handle)
    {
        var ret = cnc_allclibhndl3(host, (ushort)port, timeout, out ushort hndl);
        handle = hndl;
        return ret;
    }

    public int Disconnect(int handle)
    {
        return cnc_freelibhndl((ushort)handle);
    }

    public int ReadPmc(int handle, int adrType, int startAddr, int length, byte[] data)
    {
        var pmc = new IODBPMC
        {
            type_a = (short)adrType,
            type_d = 0, // byte type
            datano_s = (ushort)startAddr,
            datano_e = (ushort)(startAddr + length - 1),
        };

        var totalLen = (ushort)(8 + length); // header(8) + data
        var ret = pmc_rdpmcrng((ushort)handle, (short)adrType, 0,
            (ushort)startAddr, (ushort)(startAddr + length - 1), totalLen, pmc);

        if (ret == EW_OK)
            Array.Copy(pmc.cdata, data, Math.Min(length, pmc.cdata.Length));

        return ret;
    }

    public int WritePmc(int handle, int adrType, int startAddr, int length, byte[] data)
    {
        var pmc = new IODBPMC
        {
            type_a = (short)adrType,
            type_d = 0,
            datano_s = (ushort)startAddr,
            datano_e = (ushort)(startAddr + length - 1),
        };
        Array.Copy(data, pmc.cdata, Math.Min(length, pmc.cdata.Length));

        var totalLen = (ushort)(8 + length);
        return pmc_wrpmcrng((ushort)handle, totalLen, pmc);
    }

    public int ReadActPos(int handle, out double[] positions)
    {
        positions = [];
        var ret = ReadAxisPositions(handle, out var axisPositions);
        if (ret == EW_OK)
            positions = axisPositions.Select(position => position.Absolute).ToArray();
        return ret;
    }

    public int ReadAxisPositions(int handle, out FocasAxisPosition[] positions)
    {
        positions = [];
        var pos = new ODBPOS();
        short axisCount = 8;
        var ret = cnc_rdposition((ushort)handle, -1, ref axisCount, pos);

        if (ret == EW_OK)
        {
            var axes = new[] { pos.p1, pos.p2, pos.p3, pos.p4, pos.p5, pos.p6, pos.p7, pos.p8 };
            positions = axes
                .Take(Math.Min(axisCount, (short)axes.Length))
                .Select(axis => new FocasAxisPosition
                {
                    Absolute = ScalePosition(axis.abs),
                    Relative = ScalePosition(axis.rel)
                })
                .ToArray();
        }

        return ret;
    }

    public int ReadSpindleSpeed(int handle, out int speed)
    {
        speed = 0;
        var act = new ODBACT();
        var ret = cnc_acts((ushort)handle, act);

        if (ret == EW_OK)
            speed = act.data;

        return ret;
    }

    public int ReadActualFeed(int handle, out int feed)
    {
        feed = 0;
        var act = new ODBACT();
        var ret = cnc_actf((ushort)handle, act);
        if (ret == EW_OK)
            feed = act.data;
        return ret;
    }

    public int ReadFeedOverride(int handle, out int overridePercent)
    {
        overridePercent = 0;
        var pmc = new IODBPMC();
        var ret = pmc_rdpmcrng((ushort)handle, 0, 1, 12, 13, 10, pmc);
        if (ret == EW_OK)
            overridePercent = 100 - (pmc.cdata[0] - 155);
        return ret;
    }

    public int ReadSysInfo(int handle, out FocasSystemInfo info)
    {
        info = new();
        var sys = new ODBSYS();
        var ret = cnc_sysinfo((ushort)handle, sys);
        if (ret == EW_OK)
        {
            info = new()
            {
                MaxAxis = sys.max_axis,
                CncTypeCode = GetChars(sys.cnc_type),
                Series = GetChars(sys.series),
                Version = GetChars(sys.version),
                AxisCount = ParseAxisCount(sys.axes)
            };
        }
        return ret;
    }

    public int ReadAlarm(int handle, out int alarmNo, out string message)
    {
        alarmNo = 0;
        message = "";

        var alm = new ODBALM();
        var ret = cnc_alarm((ushort)handle, alm);

        if (ret != EW_OK)
            return ret;

        if (alm.data == 0)
            return EW_OK; // No alarm

        // Read alarm message
        short num = 1;
        var msgs = new ODBALMMSG[1] { new() };
        var ret2 = cnc_rdalmmsg((ushort)handle, -1, ref num, msgs);

        if (ret2 == EW_OK && num > 0)
        {
            alarmNo = msgs[0].alm_no;
            message = msgs[0].alm_msg?.TrimEnd('\0') ?? $"Alarm {alarmNo}";
        }
        else
        {
            alarmNo = alm.data;
            message = $"Alarm type {alm.data}";
        }

        return EW_OK;
    }

    public int ReadRunStatus(int handle, out int status)
    {
        status = 0;
        var stat = new ODBST();
        var ret = cnc_statinfo((ushort)handle, stat);

        if (ret == EW_OK)
        {
            // run: 0=RESET, 1=STOP, 2=HOLD, 3=START, 4=MSTR
            status = stat.run >= 3 ? 1 : 0; // 1=running, 0=stopped
        }

        return ret;
    }

    public int ReadProgramNumber(int handle, out int progNum)
    {
        progNum = 0;
        var prog = new ODBPRO();
        var ret = cnc_rdprgnum((ushort)handle, prog);

        if (ret == EW_OK)
            progNum = prog.data;

        return ret;
    }

    public int ReadProgramInfo(int handle, out FocasProgramInfo program)
    {
        program = new();
        var ret = ReadProgramName(handle, out var programName);
        if (ret != EW_OK)
            return ret;

        var prog = new ODBPRO();
        ret = cnc_rdprgnum((ushort)handle, prog);
        if (ret == EW_OK)
        {
            program = new()
            {
                MainNumber = prog.mdata,
                RunningNumber = prog.data,
                Name = programName
            };
        }
        return ret;
    }

    public int ReadProgramName(int handle, out string programName)
    {
        programName = string.Empty;
        var program = new ODBEXEPRG();
        var ret = cnc_exeprgname((ushort)handle, program);
        if (ret == EW_OK)
            programName = GetChars(program.name).TrimEnd('\0', ' ');
        return ret;
    }

    public int ReadStatusInfo(int handle, out FocasStatusInfo status)
    {
        status = new();
        var stat = new ODBST();
        var ret = cnc_statinfo((ushort)handle, stat);
        if (ret == EW_OK)
        {
            status = new()
            {
                Run = stat.run,
                Auto = stat.aut,
                Motion = stat.motion,
                Mstb = stat.mstb,
                Emergency = stat.emergency,
                Alarm = stat.alarm,
                Edit = stat.edit
            };
        }
        return ret;
    }

    public int ReadProgramBlock(int handle, out string block)
    {
        ushort length = 256;
        var buffer = new byte[length];
        var ret = cnc_rdexecprog((ushort)handle, ref length, buffer);
        block = ret == EW_OK ? System.Text.Encoding.ASCII.GetString(buffer, 0, length).TrimEnd('\0', ' ', '\r', '\n') : string.Empty;
        return ret;
    }

    public int ReadActualFeedRate(int handle, out double feedRate)
    {
        feedRate = 0;
        var speed = new ODBSPEED();
        var ret = cnc_rdspeed((ushort)handle, -1, speed);
        if (ret == EW_OK)
            feedRate = speed.actf.dec > 0 ? speed.actf.data / Math.Pow(10, speed.actf.dec) : speed.actf.data;
        return ret;
    }

    public int ReadCommandedFeedRate(int handle, out double feedRate)
    {
        var ret = ReadModalValue(handle, 103, out feedRate);
        return ret;
    }

    public int ReadToolId(int handle, out int toolId)
    {
        var ret = ReadModalValue(handle, 108, out var value);
        toolId = (int)Math.Round(value);
        return ret;
    }

    public int ReadMaxToolGroup(int handle, out int maxGroup)
    {
        maxGroup = 0;
        var group = new ODBLFNO();
        var ret = cnc_rdmaxgrp((ushort)handle, group);
        if (ret == EW_OK)
            maxGroup = group.data;
        return ret;
    }

    public int ReadMacroVariable(int handle, int number, out double value)
    {
        value = 0;
        var macro = new ODBM();
        var ret = cnc_rdmacro((ushort)handle, (short)number, 10, macro);
        if (ret == EW_OK)
            value = macro.mcr_val * Math.Pow(10, -macro.dec_val);
        return ret;
    }

    public int ReadParameter(int handle, int number, out int value)
    {
        value = 0;
        var parameter = new IODBPSD_1();
        var ret = cnc_rdparam((ushort)handle, (short)number, 0, 8, parameter);
        if (ret == EW_OK)
            value = parameter.ldata;
        return ret;
    }

    public int StartProgramDownload(int handle) => cnc_dwnstart((ushort)handle);

    public int DownloadProgramChunk(int handle, byte[] data, int length)
        => cnc_download((ushort)handle, data, (short)length);

    public int EndProgramDownload(int handle) => cnc_dwnend((ushort)handle);

    public int StartProgramDownloadAtPath(int handle, string destinationPath)
        => cnc_dwnstart4((ushort)handle, 0, destinationPath);

    public int DownloadProgramChunkAtPath(int handle, byte[] data, int length, out int acceptedLength)
    {
        acceptedLength = length;
        object chunk = length == data.Length ? data : data[..length];
        return cnc_download4((ushort)handle, ref acceptedLength, chunk);
    }

    public int EndProgramDownloadAtPath(int handle) => cnc_dwnend4((ushort)handle);

    public int StartProgramUpload(int handle, short programNumber)
        => cnc_upstart((ushort)handle, programNumber);

    public int UploadProgramChunk(int handle, byte[] buffer, out int actualLength)
    {
        actualLength = 0;
        var upload = new ODBUP();
        ushort requestedLength = (ushort)Math.Min(buffer.Length, upload.data.Length);
        var ret = cnc_upload((ushort)handle, upload, ref requestedLength);
        if (ret == EW_OK && requestedLength > 0)
        {
            actualLength = requestedLength;
            Array.Copy(upload.data, buffer, actualLength);
        }
        return ret;
    }

    public int EndProgramUpload(int handle) => cnc_upend((ushort)handle);

    public int StartProgramUploadFromPath(int handle, string sourcePath)
    {
        object path = sourcePath;
        return cnc_upstart4((ushort)handle, 0, path);
    }

    public int UploadProgramChunkFromPath(int handle, byte[] buffer, out int actualLength)
    {
        actualLength = 0;
        var requestedLength = buffer.Length;
        object data = buffer;
        var ret = cnc_upload4((ushort)handle, ref requestedLength, data);
        if (ret == EW_OK && requestedLength > 0)
        {
            actualLength = requestedLength;
        }
        return ret;
    }

    public int EndProgramUploadFromPath(int handle) => cnc_upend4((ushort)handle);

    public int ReadProgramDirectory(int handle, out IReadOnlyList<FocasProgramDirectoryEntry> entries)
        => ReadProgramDirectoryPages((topNumber, requestedCount) =>
        {
            short actualCount = requestedCount;
            var buffer = new PRGDIR3[requestedCount];
            var startNumber = topNumber;
            var ret = cnc_rdprogdir3((ushort)handle, 2, ref startNumber, ref actualCount, buffer);
            IReadOnlyList<FocasProgramDirectoryEntry> pageEntries = ret == EW_OK
                ? buffer.Take(actualCount).Select(MapProgramDirectoryEntry).ToList()
                : Array.Empty<FocasProgramDirectoryEntry>();
            var nextTopNumber = ret == EW_OK && actualCount > 0
                ? buffer[actualCount - 1].number + 1
                : startNumber;
            return (ret, nextTopNumber, pageEntries);
        }, out entries);

    public FocasDetailError? ReadDetailError(int handle)
    {
        var error = new ODBERR();
        var ret = cnc_getdtailerr((ushort)handle, error);
        return ret == EW_OK ? new FocasDetailError(error.err_no, error.err_dtno) : null;
    }

    public int ReadCncMemoryDirectory(int handle, string path, out IReadOnlyList<ProgramFileEntry> entries)
    {
        var normalizedPath = NormalizePdfDirectoryPath(path);
        var subdirRc = ReadPdfSubDirectories((ushort)handle, normalizedPath, out var directories);
        if (subdirRc != EW_OK) { entries = directories; return subdirRc; }
        var fileRc = ReadPdfFiles((ushort)handle, normalizedPath, out var files);
        if (fileRc != EW_OK) { entries = [.. directories, .. files]; return fileRc; }
        entries = [.. directories, .. files];
        return EW_OK;
    }

    public int ReadAlarmStatus(int handle, out int statusCode, out string message)
    {
        statusCode = 0;
        message = string.Empty;
        var ret = cnc_alarm2((ushort)handle, out statusCode);
        if (ret == EW_OK)
            message = MapAlarmStatus(statusCode);
        return ret;
    }

    #endregion

    private static int ReadProgramDirectoryPages(
        Func<int, short, (int ReturnCode, int NextTopProgram, IReadOnlyList<FocasProgramDirectoryEntry> Entries)> readPage,
        out IReadOnlyList<FocasProgramDirectoryEntry> entries, short pageSize = 20)
    {
        var allEntries = new List<FocasProgramDirectoryEntry>();
        var topProgram = 0;
        while (true)
        {
            var (returnCode, nextTopProgram, pageEntries) = readPage(topProgram, pageSize);
            if (returnCode == EW_NUMBER) break;
            if (returnCode != EW_OK) { entries = allEntries; return returnCode; }
            allEntries.AddRange(pageEntries);
            if (pageEntries.Count < pageSize) break;
            topProgram = nextTopProgram;
        }
        entries = allEntries;
        return EW_OK;
    }

    private static int ReadPdfSubDirectories(ushort handle, string path, out IReadOnlyList<ProgramFileEntry> entries)
    {
        var items = new List<ProgramFileEntry>();
        for (short index = 1; index < short.MaxValue; index++)
        {
            short count = 1;
            var request = new IDBPDFSDIR { path = path, req_num = index };
            var raw = new ODBPDFSDIR();
            var ret = cnc_rdpdf_subdir(handle, ref count, request, raw);
            var name = CleanPdfString(raw.d_f);
            if (ret == EW_NUMBER || count <= 0 || string.IsNullOrWhiteSpace(name))
                break;
            if (ret != EW_OK) { entries = items; return ret; }
            items.Add(new()
            {
                Path = CombineCncMemoryPath(path, name),
                Name = name,
                IsDirectory = true,
                CanDownload = false,
                CanUpload = true,
                HasChildren = true
            });
        }
        entries = items;
        return EW_OK;
    }

    private static int ReadPdfFiles(ushort handle, string path, out IReadOnlyList<ProgramFileEntry> entries)
    {
        var items = new List<ProgramFileEntry>();
        for (short index = 1; index < short.MaxValue; index++)
        {
            short count = 1;
            object request = new IDBPDFADIR { path = path, req_num = index, size_kind = 0, type = 0 };
            object raw = new ODBPDFADIR();
            var ret = cnc_rdpdf_alldir(handle, ref count, request, raw);
            var entry = (ODBPDFADIR)raw;
            var name = CleanPdfString(entry.d_f);
            if (ret == EW_NUMBER || count <= 0 || string.IsNullOrWhiteSpace(name))
                break;
            if (ret != EW_OK) { entries = items; return ret; }
            items.Add(MapPdfFileEntry(path, entry, name));
        }
        entries = items;
        return EW_OK;
    }

    private static ProgramFileEntry MapPdfFileEntry(string parentPath, ODBPDFADIR entry, string name) => new()
    {
        Path = CombineCncMemoryPath(parentPath, name),
        Name = name,
        IsDirectory = false,
        SizeBytes = entry.size,
        ModifiedAt = TryCreateTimestamp(entry.year, entry.mon, entry.day, entry.hour, entry.min, entry.sec),
        CanDownload = true,
        CanUpload = false,
        HasChildren = false,
        Comment = CleanPdfString(entry.comment)
    };

    private static DateTimeOffset? TryCreateTimestamp(int year, int month, int day, int hour, int minute, int second)
    {
        if (year is < 1 or > 9999)
            return null;
        if (month is < 1 or > 12)
            return null;
        if (day is < 1 or > 31)
            return null;
        if (hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59)
            return null;

        var maxDay = DateTime.DaysInMonth(year, month);
        return day > maxDay
            ? null
            : new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
    }

    private static string NormalizePdfDirectoryPath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        if (!normalized.EndsWith('/'))
            normalized += "/";
        return normalized;
    }

    private static string CombineCncMemoryPath(string parentPath, string childName)
        => $"{parentPath.TrimEnd('/')}/{childName.Trim()}";

    private static string CleanPdfString(string value) => value.TrimEnd('\0', ' ');

    private static FocasProgramDirectoryEntry MapProgramDirectoryEntry(PRGDIR3 raw)
    {
        var d = raw.mdate;
        DateTimeOffset? modified = d.year > 0
            ? new DateTimeOffset(d.year, d.month, d.day, d.hour, d.minute, 0, TimeSpan.Zero)
            : null;
        return new() { Number = raw.number, Size = raw.length, Comment = raw.comment?.TrimEnd('\0', ' ') ?? string.Empty, ModifiedDate = modified };
    }

    private static int ReadModalValue(int handle, short type, out double value)
    {
        value = 0;
        var modal = new ODBMDL();
        short length = (short)Marshal.SizeOf<ODBMDL>();
        var ret = cnc_modal((ushort)handle, type, 0, length, modal);
        if (ret == EW_OK)
            value = modal.flag2 > 0 ? modal.aux_data / Math.Pow(10, modal.flag2) : modal.aux_data;
        return ret;
    }

    private static string GetChars(char[] chars) => new string(chars).TrimEnd('\0', ' ');

    private static int ParseAxisCount(char[] chars) =>
        int.TryParse(GetChars(chars), out var axisCount) ? axisCount : 0;

    private static string MapAlarmStatus(int statusCode) => statusCode switch
    {
        0 => "参数开启（SW）",
        1 => "关机参数设置（PW）",
        2 => "I / O错误（IO）",
        3 => "前景P / S（PS",
        4 => "超程，外部数据（OT",
        5 => "过热报警（OH）",
        6 => "伺服报警（SV",
        7 => "数据I / O错误（SR）",
        8 => "宏指令报警（MC",
        9 => "主轴报警（SP）",
        10 => "其他警报（DS）",
        11 => "有关故障防止功能（IE）的警报",
        12 => "背景P / S（BG）",
        13 => "同步错误（SN）",
        14 => "保留",
        15 => "外部报警信息（EX）",
        16 => "正向超程（软限位1）",
        _ => "未知错误"
    };

    private static double ScalePosition(POSELM position) =>
        position.data * Math.Pow(10, -position.dec);
}
