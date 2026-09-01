namespace IndustrialIoT.Protocols.Gsk;

using System.Runtime.InteropServices;

public sealed partial class NativeGskrmApi
{
    [DllImport(DllName, EntryPoint = "GSKRM_GetFeedRate", CallingConvention = Conv)]
    private static extern int GSKRM_GetFeedRate(IntPtr handle, out double rate);

    [DllImport(DllName, EntryPoint = "GSKRM_GetFastRate", CallingConvention = Conv)]
    private static extern int GSKRM_GetFastRate(IntPtr handle, out double rate);

    [DllImport(DllName, EntryPoint = "GSKRM_GetJogRate", CallingConvention = Conv)]
    private static extern int GSKRM_GetJogRate(IntPtr handle, out double rate);

    [DllImport(DllName, EntryPoint = "GSKRM_GetSpindleRate", CallingConvention = Conv)]
    private static extern int GSKRM_GetSpindleRate(IntPtr handle, out double rate);

    [DllImport(DllName, EntryPoint = "GSKRM_GetHandWheelRate", CallingConvention = Conv)]
    private static extern int GSKRM_GetHandWheelRate(IntPtr handle, out double rate);

    [DllImport(DllName, EntryPoint = "GSKRM_GetFeedSpeedAct", CallingConvention = Conv)]
    private static extern int GSKRM_GetFeedSpeedAct(IntPtr handle, out double feedMmPerMin);

    [DllImport(DllName, EntryPoint = "GSKRM_GetFeedSpeedProg", CallingConvention = Conv)]
    private static extern int GSKRM_GetFeedSpeedProg(IntPtr handle, out double feedMmPerMin);

    [DllImport(DllName, EntryPoint = "GSKRM_GetSpindleSpeedAct", CallingConvention = Conv)]
    private static extern int GSKRM_GetSpindleSpeedAct(IntPtr handle, int spdnum, out double rpm);

    [DllImport(DllName, EntryPoint = "GSKRM_GetSpindleSpeedProg", CallingConvention = Conv)]
    private static extern int GSKRM_GetSpindleSpeedProg(IntPtr handle, int spdnum, out double rpm);

    public int GetFeedSpeedAct(int handle, out int feedMmPerMin)
        => GetDoubleAsInt(handle, GSKRM_GetFeedSpeedAct, out feedMmPerMin);

    public int GetFeedSpeedProg(int handle, out int feedMmPerMin)
        => GetDoubleAsInt(handle, GSKRM_GetFeedSpeedProg, out feedMmPerMin);

    public int GetSpindleSpeedAct(int handle, out int rpm)
        => GetSpindleDoubleAsInt(handle, GSKRM_GetSpindleSpeedAct, out rpm);

    public int GetSpindleSpeedProg(int handle, out int rpm)
        => GetSpindleDoubleAsInt(handle, GSKRM_GetSpindleSpeedProg, out rpm);

    public int GetAllRateInfo(int handle, out GskrmRateInfo rates)
    {
        rates = new GskrmRateInfo { FeedRate = 0, FastRate = 0, JogRate = 0, SpindleRate = 0, HandWheelRate = 0 };
        var native = Resolve(handle);
        if (native == IntPtr.Zero)
            return GskrmErrorCodes.InvalidHandle;
        double feed;
        double fast = 0;
        double jog = 0;
        double spindle = 0;
        double handWheel = 0;
        int rc = GSKRM_GetFeedRate(native, out feed);
        if (rc == GskrmErrorCodes.Ok) rc = GSKRM_GetFastRate(native, out fast);
        if (rc == GskrmErrorCodes.Ok) rc = GSKRM_GetJogRate(native, out jog);
        if (rc == GskrmErrorCodes.Ok) rc = GSKRM_GetSpindleRate(native, out spindle);
        if (rc == GskrmErrorCodes.Ok) rc = GSKRM_GetHandWheelRate(native, out handWheel);
        rates = new GskrmRateInfo
        {
            FeedRate = (int)Math.Round(feed),
            FastRate = (int)Math.Round(fast),
            JogRate = (int)Math.Round(jog),
            SpindleRate = (int)Math.Round(spindle),
            HandWheelRate = (int)Math.Round(handWheel)
        };
        return rc;
    }

    private int GetDoubleAsInt(int handle, NativeDoubleRead read, out int value)
    {
        value = 0;
        var native = Resolve(handle);
        if (native == IntPtr.Zero)
            return GskrmErrorCodes.InvalidHandle;
        int rc = read(native, out double raw);
        value = (int)Math.Round(raw);
        return rc;
    }

    private int GetSpindleDoubleAsInt(int handle, NativeSpindleDoubleRead read, out int value)
    {
        value = 0;
        var native = Resolve(handle);
        if (native == IntPtr.Zero)
            return GskrmErrorCodes.InvalidHandle;
        int rc = read(native, 1, out double raw);
        value = (int)Math.Round(raw);
        return rc;
    }

    private delegate int NativeDoubleRead(IntPtr handle, out double value);
    private delegate int NativeSpindleDoubleRead(IntPtr handle, int spdnum, out double value);
}
