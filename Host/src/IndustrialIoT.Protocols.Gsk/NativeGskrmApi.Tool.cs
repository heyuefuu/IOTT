namespace IndustrialIoT.Protocols.Gsk;

using System.Runtime.InteropServices;

public sealed partial class NativeGskrmApi
{
    [DllImport(DllName, EntryPoint = "GSKRM_GetToolOffsetCount", CallingConvention = Conv)]
    private static extern int GSKRM_GetToolOffsetCount(IntPtr handle, out int count);

    [DllImport(DllName, EntryPoint = "GSKRM_GetToolOffsetValue", CallingConvention = Conv)]
    private static extern int GSKRM_GetToolOffsetValue(IntPtr handle, int index, out double value);

    [DllImport(DllName, EntryPoint = "GSKRM_GetToolOffsetValueEx", CallingConvention = Conv)]
    private static extern int GSKRM_GetToolOffsetValueEx(IntPtr handle, int index, int type, out double value);

    public int GetToolOffsetCount(int handle, out int count)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero) { count = 0; return GskrmErrorCodes.InvalidHandle; }
        return GSKRM_GetToolOffsetCount(native, out count);
    }

    public int GetToolOffsetValue(int handle, int index, out double value)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero) { value = 0; return GskrmErrorCodes.InvalidHandle; }
        return GSKRM_GetToolOffsetValue(native, index, out value);
    }
}
