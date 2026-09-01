namespace IndustrialIoT.Protocols.JingDiao;

using System.Runtime.InteropServices;
using System.Text;

public sealed class NativeJdMonApi : IJdMonApi
{
    private const string DllName = "NcMonIO.dll";
    private static readonly Encoding Gbk;

    static NativeJdMonApi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding(936);
    }

    public IntPtr Create() => CreateJDMachMon();
    public void Delete(ref IntPtr handle) => DeleteJDMachMon(ref handle);
    public bool Connect(IntPtr handle, string host, int rpcPort, int callbackPort, int uploadPort, int downloadPort)
        => ConnectJDMach(handle, host, (ushort)rpcPort, (ushort)callbackPort, (ushort)uploadPort, (ushort)downloadPort) != 0;
    public bool Disconnect(IntPtr handle) => DisconnectJDMach(handle) != 0;
    public bool IsConnected(IntPtr handle) => IsConnect(handle) != 0;
    public void SetConnectionTimeout(IntPtr handle, int timeoutMs) => SetConnectionTimeoutNative(handle, timeoutMs);
    public void SetRpcTimeout(IntPtr handle, int timeoutMs) => SetRPCTimeout(handle, timeoutMs);
    public uint GetLastError(IntPtr handle) => GetLastErr(handle);
    public bool GetMachPos(IntPtr handle, double[] machine, double[] absolute, double[] relative)
        => GetMachPosNative(handle, machine, absolute, relative) != 0;
    public bool GetProgState(IntPtr handle, out int state) => GetProgStateNative(handle, out state) != 0;
    public bool GetAlarm(IntPtr handle, out int alarm) => GetMachAlmInfo(handle, out alarm) != 0;
    public bool GetSpindle(IntPtr handle, double[] spindle) => GetSpindleInfo(handle, spindle) != 0;
    public bool GetRate(IntPtr handle, int[] rates) => GetRateNative(handle, rates) != 0;
    public bool SendNcFile(IntPtr handle, string localPath, bool addToTask, bool setMainProgram)
        => SendNcFileNative(handle, localPath, addToTask ? 1 : 0, setMainProgram ? 1 : 0, IntPtr.Zero, IntPtr.Zero, 0) != 0;
    public bool ReceiveFile(IntPtr handle, string remotePath, string localPath)
        => ReceiveFileNative(handle, remotePath, localPath, IntPtr.Zero, IntPtr.Zero) != 0;
    public bool DeleteFile(IntPtr handle, string directory, string fileName)
        => DelMachFile(handle, directory, fileName) != 0;

    public bool GetBasicModal(IntPtr handle, out JingDiaoModalSnapshot value)
    {
        var workCoordinate = 0;
        var feedrate = 0f;
        var spindleSpeed = 0;
        var toolNo = 0;
        var machiningTime = 0f;
        var programNo = 0;
        var mainProgramNo = 0;
        var ok = GetBasicModalInfo(handle, ref workCoordinate, ref feedrate, ref spindleSpeed, ref toolNo,
            ref machiningTime, ref programNo, ref mainProgramNo) != 0;
        value = new(workCoordinate, feedrate, spindleSpeed, toolNo, machiningTime, programNo, mainProgramNo);
        return ok;
    }

    public bool GetMacro(IntPtr handle, int number, out double value)
    {
        var numbers = new int[64];
        var values = new double[64];
        numbers[0] = number;
        var ok = GetMacroVarValue(handle, 1, numbers, values) != 0;
        value = values[0];
        return ok;
    }

    public bool GetLineNo(IntPtr handle, out int lineNo)
    {
        var values = new int[1];
        var ok = GetCurLineNo(handle, values) != 0;
        lineNo = values[0];
        return ok;
    }

    public bool GetPartCount(IntPtr handle, out int count)
        => GetMachinedWorkpieceCount(handle, out count) != 0;

    public bool GetMachFileList(IntPtr handle, string directory, int bufferSize, out string fileList)
    {
        var buffer = new byte[Math.Max(1024, bufferSize)];
        var ok = GetMachFileListNative(handle, directory, buffer.Length, buffer) != 0;
        var len = Array.IndexOf(buffer, (byte)0);
        fileList = Gbk.GetString(buffer, 0, len >= 0 ? len : buffer.Length);
        return ok;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern IntPtr CreateJDMachMon();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern void DeleteJDMachMon(ref IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int ConnectJDMach(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string host,
        ushort rpcPort, ushort callbackPort, ushort uploadPort, ushort downloadPort);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int DisconnectJDMach(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int IsConnect(IntPtr handle);

    [DllImport(DllName, EntryPoint = "SetConnectionTimeout", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern void SetConnectionTimeoutNative(IntPtr handle, int timeoutMs);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern void SetRPCTimeout(IntPtr handle, int timeoutMs);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern uint GetLastErr(IntPtr handle);

    [DllImport(DllName, EntryPoint = "GetMachPos", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int GetMachPosNative(IntPtr handle, [Out] double[] machine, [Out] double[] absolute, [Out] double[] relative);

    [DllImport(DllName, EntryPoint = "GetProgState", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int GetProgStateNative(IntPtr handle, out int state);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int GetMachAlmInfo(IntPtr handle, out int alarm);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int GetBasicModalInfo(IntPtr handle, ref int workCoordinate, ref float feedrate,
        ref int spindleSpeed, ref int toolNo, ref float machiningTime, ref int programNo, ref int mainProgramNo);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int GetSpindleInfo(IntPtr handle, [Out] double[] spindle);

    [DllImport(DllName, EntryPoint = "GetRate", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int GetRateNative(IntPtr handle, [Out] int[] rates);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int GetMacroVarValue(IntPtr handle, int count, [In] int[] numbers, [Out] double[] values);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int GetCurLineNo(IntPtr handle, [Out] int[] lineNo);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int GetMachinedWorkpieceCount(IntPtr handle, out int count);

    [DllImport(DllName, EntryPoint = "GetMachFileList", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int GetMachFileListNative(
        IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string directory, int bufferSize, [Out] byte[] fileList);

    [DllImport(DllName, EntryPoint = "SendNcFile", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int SendNcFileNative(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string fileName,
        int addToTask, int setMainProgram, IntPtr progressControl, IntPtr progressCallback, int fileThread);

    [DllImport(DllName, EntryPoint = "ReceiveFile", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int ReceiveFileNative(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string sourceFileName,
        [MarshalAs(UnmanagedType.LPStr)] string destinationFileName, IntPtr progressControl, IntPtr progressCallback);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int DelMachFile(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string directory,
        [MarshalAs(UnmanagedType.LPStr)] string fileName);
}
