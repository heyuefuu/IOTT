namespace IndustrialIoT.Protocols.Gsk;

using System.Runtime.InteropServices;

public sealed partial class NativeGskrmApi
{
    [DllImport(DllName, EntryPoint = "GSKRM_GetMacroValue", CallingConvention = Conv)]
    private static extern int GSKRM_GetMacroValue(IntPtr handle, int number, out double value, out int empty);

    [DllImport(DllName, EntryPoint = "GSKRM_SetMacroValue", CallingConvention = Conv)]
    private static extern int GSKRM_SetMacroValue(IntPtr handle, int number, double value);

    public int GetMacroValue(int handle, int number, out double value)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero) { value = 0; return GskrmErrorCodes.InvalidHandle; }
        return GSKRM_GetMacroValue(native, number, out value, out _);
    }

    public int SetMacroValue(int handle, int number, double value)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero)
            return GskrmErrorCodes.InvalidHandle;
        return GSKRM_SetMacroValue(native, number, value);
    }
}
