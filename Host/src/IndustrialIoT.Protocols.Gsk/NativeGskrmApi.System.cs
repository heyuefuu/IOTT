namespace IndustrialIoT.Protocols.Gsk;

using System.Runtime.InteropServices;
using System.Text;

public sealed partial class NativeGskrmApi
{
    [DllImport(DllName, EntryPoint = "GSKRM_GetCncInfo", CallingConvention = Conv, CharSet = CharSet.Ansi)]
    private static extern int GSKRM_GetCncInfo(IntPtr handle,
        StringBuilder machineType, int typeLen,
        StringBuilder seriesLabel, int labelLen,
        out int pathCount, out int axisCount,
        StringBuilder version, int versionLen);

    [DllImport(DllName, EntryPoint = "GSKRM_GetCncTypeName", CallingConvention = Conv, CharSet = CharSet.Ansi)]
    private static extern int GSKRM_GetCncTypeName(IntPtr handle, StringBuilder name);

    [DllImport(DllName, EntryPoint = "GSKRM_GetCncPathInfo", CallingConvention = Conv)]
    private static extern int GSKRM_GetCncPathInfo(IntPtr handle, out int pathCount, out int activePath);

    [DllImport(DllName, EntryPoint = "GSKRM_GetAvailAxisCount", CallingConvention = Conv)]
    private static extern int GSKRM_GetAvailAxisCount(IntPtr handle, out uint count);

    [DllImport(DllName, EntryPoint = "GSKRM_GetAvailAxisName", CallingConvention = Conv, CharSet = CharSet.Ansi)]
    private static extern int GSKRM_GetAvailAxisName(IntPtr handle, int type, byte[] axisName, uint axisCount);

    public int GetCncInfo(int handle, out GskrmCncInfo info)
    {
        var type = new StringBuilder(DefaultStringBuffer);
        var label = new StringBuilder(DefaultStringBuffer);
        var version = new StringBuilder(DefaultStringBuffer);
        var native = Resolve(handle);
        if (native == IntPtr.Zero)
        {
            info = new GskrmCncInfo { MachineType = "", SeriesLabel = "", PathCount = 0, AxisCount = 0 };
            return GskrmErrorCodes.InvalidHandle;
        }
        int rc = GSKRM_GetCncInfo(native, type, type.Capacity, label, label.Capacity,
            out int pathCount, out int axisCount, version, version.Capacity);
        info = new GskrmCncInfo
        {
            MachineType = type.ToString(),
            SeriesLabel = label.ToString(),
            PathCount = pathCount,
            AxisCount = axisCount,
            Version = version.Length > 0 ? version.ToString() : null
        };
        return rc;
    }

    public int GetCncTypeName(int handle, out string name)
    {
        var sb = new StringBuilder(DefaultStringBuffer);
        var native = Resolve(handle);
        if (native == IntPtr.Zero) { name = ""; return GskrmErrorCodes.InvalidHandle; }
        int rc = GSKRM_GetCncTypeName(native, sb);
        name = sb.ToString();
        return rc;
    }

    public int GetAvailAxisCount(int handle, out int count)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero) { count = 0; return GskrmErrorCodes.InvalidHandle; }
        int rc = GSKRM_GetAvailAxisCount(native, out uint value);
        count = checked((int)value);
        return rc;
    }

    public int GetAvailAxisName(int handle, int axisIndex, out string name)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero) { name = ""; return GskrmErrorCodes.InvalidHandle; }
        int axisCount = Math.Max(axisIndex + 1, 1);
        var buffer = new byte[axisCount * 8];
        int rc = GSKRM_GetAvailAxisName(native, 0, buffer, (uint)axisCount);
        var all = ReadCString(buffer);
        name = axisIndex < all.Length ? all.Substring(axisIndex, 1) : "";
        return rc;
    }

    public int GetAvailAxisUnits(int handle, int axisIndex, out string unit)
    {
        unit = "mm";
        return Resolve(handle) == IntPtr.Zero ? GskrmErrorCodes.InvalidHandle : GskrmErrorCodes.Ok;
    }
}
