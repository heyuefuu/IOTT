namespace IndustrialIoT.Protocols.Gsk;

public sealed partial class GskrmIpcClient
{
    public int GetPosition(int handle, int axisIndex, out GskrmPosition position)
    {
        int rc = PostValue<GskrmPosition>("api/gskrm/get-position",
            new GskrmAxisRequest(handle, axisIndex), out var value);
        position = value ?? new GskrmPosition { Absolute = 0, Machine = 0, Relative = 0 };
        return rc;
    }

    public int GetFecPoint(int handle, int axisIndex, out double value)
    {
        int rc = PostValue<double>("api/gskrm/get-fec-point",
            new GskrmAxisRequest(handle, axisIndex), out var result);
        value = result;
        return rc;
    }
}
