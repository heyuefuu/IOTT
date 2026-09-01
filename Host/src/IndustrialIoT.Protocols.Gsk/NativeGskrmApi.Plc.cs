namespace IndustrialIoT.Protocols.Gsk;

using System.Runtime.InteropServices;

public sealed partial class NativeGskrmApi
{
    [DllImport(DllName, EntryPoint = "GSKRM_GetPlcData", CallingConvention = Conv)]
    private static extern int GSKRM_GetPlcData(IntPtr handle, short type, short start, short count, byte[] value);

    [DllImport(DllName, EntryPoint = "GSKRM_SetPlcData", CallingConvention = Conv)]
    private static extern int GSKRM_SetPlcData(IntPtr handle, int type, int count, int num, byte[] value);

    public int GetPlcData(int handle, string address, int length, byte[] buffer)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero)
            return GskrmErrorCodes.InvalidHandle;
        if (!TryParsePlcAddress(address, out short type, out short start, out short count))
            return GskrmErrorCodes.InvalidArgument;
        count = (short)Math.Min(count, length);
        return GSKRM_GetPlcData(native, type, start, count, buffer);
    }

    public int SetPlcData(int handle, string address, byte[] data)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero)
            return GskrmErrorCodes.InvalidHandle;
        if (!TryParsePlcAddress(address, out short type, out short start, out short count))
            return GskrmErrorCodes.InvalidArgument;
        return GSKRM_SetPlcData(native, type, Math.Min(count, data.Length), start, data);
    }

    private static bool TryParsePlcAddress(string address, out short type, out short start, out short count)
    {
        type = 0;
        start = 0;
        count = 0;
        var parts = address.Split([':', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 3
            && short.TryParse(parts[0], out type)
            && short.TryParse(parts[1], out start)
            && short.TryParse(parts[2], out count);
    }
}
