namespace IndustrialIoT.Protocols.Gsk;

using System.Runtime.InteropServices;

public sealed partial class NativeGskrmApi
{
    [DllImport(DllName, EntryPoint = "GSKRM_GetParamValue", CallingConvention = Conv)]
    private static extern int GSKRM_GetParamValue(IntPtr handle, ushort number, int axis, out int value);

    [DllImport(DllName, EntryPoint = "GSKRM_SetParamValue", CallingConvention = Conv)]
    private static extern int GSKRM_SetParamValue(IntPtr handle, ushort number, int axis, int value);

    [DllImport(DllName, EntryPoint = "GSKRM_SetParamBitValue", CallingConvention = Conv)]
    private static extern int GSKRM_SetParamBitValue(IntPtr handle, ushort number, int axis, int bitIndex, byte value);

    [DllImport(DllName, EntryPoint = "GSKRM_GetDiagnoseValue", CallingConvention = Conv)]
    private static extern int GSKRM_GetDiagnoseValue(IntPtr handle, int number, out int value);

    public int GetParamValue(int handle, int number, int axis, out int value)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero) { value = 0; return GskrmErrorCodes.InvalidHandle; }
        return GSKRM_GetParamValue(native, checked((ushort)number), axis, out value);
    }

    public int SetParamValue(int handle, int number, int axis, int value)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero)
            return GskrmErrorCodes.InvalidHandle;
        return GSKRM_SetParamValue(native, checked((ushort)number), axis, value);
    }
}
