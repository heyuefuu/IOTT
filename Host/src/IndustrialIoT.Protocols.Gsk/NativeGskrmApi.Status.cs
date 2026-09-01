namespace IndustrialIoT.Protocols.Gsk;

using System.Runtime.InteropServices;
using System.Text;

public sealed partial class NativeGskrmApi
{
    [DllImport(DllName, EntryPoint = "GSKRM_GetCncState", CallingConvention = Conv)]
    private static extern int GSKRM_GetCncState(IntPtr handle, out uint rawState);

    [DllImport(DllName, EntryPoint = "GSKRM_GetWorkMode", CallingConvention = Conv)]
    private static extern int GSKRM_GetWorkMode(IntPtr handle, out uint workMode);

    [DllImport(DllName, EntryPoint = "GSKRM_GetRunCncProgName", CallingConvention = Conv, CharSet = CharSet.Ansi)]
    private static extern int GSKRM_GetRunCncProgName(IntPtr handle, StringBuilder name);

    [DllImport(DllName, EntryPoint = "GSKRM_GetRunLineNo", CallingConvention = Conv)]
    private static extern int GSKRM_GetRunLineNo(IntPtr handle, out int lineNo);

    public int GetCncState(int handle, out GskrmCncState state)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero)
        {
            state = new GskrmCncState { RawState = 0 };
            return GskrmErrorCodes.InvalidHandle;
        }
        int rc = GSKRM_GetCncState(native, out uint raw);
        state = new GskrmCncState
        {
            RawState = checked((int)raw),
            IsRunning = raw == 2,
            IsAlarm = false,
            EmergencyStop = false,
            WorkMode = null
        };
        return rc;
    }

    public int GetStatus(int handle, out int statusCode)
    {
        int rc = GetCncState(handle, out var state);
        statusCode = state.RawState;
        return rc;
    }

    public int GetWorkMode(int handle, out string mode)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero) { mode = ""; return GskrmErrorCodes.InvalidHandle; }
        int rc = GSKRM_GetWorkMode(native, out uint value);
        mode = value switch
        {
            0 => "EDIT",
            1 => "MEM",
            2 => "MDI",
            3 => "DNC",
            4 => "JOG",
            5 => "HANDLE",
            6 => "REF",
            _ => value.ToString()
        };
        return rc;
    }

    public int GetRunCncProgName(int handle, out string programName)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero) { programName = ""; return GskrmErrorCodes.InvalidHandle; }
        var sb = new StringBuilder(32);
        int rc = GSKRM_GetRunCncProgName(native, sb);
        programName = sb.ToString();
        return rc;
    }

    public int GetMainCncProgName(int handle, out string programName)
        => GetRunCncProgName(handle, out programName);

    public int GetRunLineNo(int handle, out int lineNo)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero) { lineNo = 0; return GskrmErrorCodes.InvalidHandle; }
        return GSKRM_GetRunLineNo(native, out lineNo);
    }

    public int GetEspState(int handle, out bool estop)
    {
        estop = false;
        return Resolve(handle) == IntPtr.Zero ? GskrmErrorCodes.InvalidHandle : GskrmErrorCodes.NotSupported;
    }
}
