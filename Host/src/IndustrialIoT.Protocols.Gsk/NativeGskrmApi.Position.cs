namespace IndustrialIoT.Protocols.Gsk;

using System.Runtime.InteropServices;

public sealed partial class NativeGskrmApi
{
    [DllImport(DllName, EntryPoint = "GSKRM_GetAvailAxisCoordinate", CallingConvention = Conv)]
    private static extern int GSKRM_GetAvailAxisCoordinate(IntPtr handle, int type, float[] coord, uint axisCount);

    public int GetPosition(int handle, int axisIndex, out GskrmPosition position)
    {
        int axisCount = Math.Max(axisIndex + 1, 1);
        var absolute = new float[axisCount];
        var machine = new float[axisCount];
        var relative = new float[axisCount];
        int rc = GetAxisCoordinate(handle, 0, absolute);
        if (rc == GskrmErrorCodes.Ok) rc = GetAxisCoordinate(handle, 2, machine);
        if (rc == GskrmErrorCodes.Ok) rc = GetAxisCoordinate(handle, 1, relative);
        position = new GskrmPosition
        {
            Absolute = absolute[axisIndex],
            Machine = machine[axisIndex],
            Relative = relative[axisIndex],
            Distance = 0
        };
        return rc;
    }

    public int GetFecPoint(int handle, int axisIndex, out double value)
    {
        int axisCount = Math.Max(axisIndex + 1, 1);
        var remain = new float[axisCount];
        int rc = GetAxisCoordinate(handle, 3, remain);
        value = remain[axisIndex];
        return rc;
    }

    private int GetAxisCoordinate(int handle, int type, float[] values)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero)
            return GskrmErrorCodes.InvalidHandle;
        return GSKRM_GetAvailAxisCoordinate(native, type, values, (uint)values.Length);
    }
}
