namespace IndustrialIoT.Protocols.Gsk;

public sealed partial class GskrmIpcClient
{
    public int GetCncInfo(int handle, out GskrmCncInfo info)
    {
        int rc = PostValue<GskrmCncInfo>("api/gskrm/get-cnc-info", new GskrmHandleRequest(handle), out var value);
        info = value ?? new GskrmCncInfo { MachineType = "", SeriesLabel = "", PathCount = 0, AxisCount = 0 };
        return rc;
    }

    public int GetCncTypeName(int handle, out string name)
        => PostString("api/gskrm/get-cnc-type-name", new GskrmHandleRequest(handle), out name);

    public int GetAvailAxisCount(int handle, out int count)
        => PostInt("api/gskrm/get-avail-axis-count", new GskrmHandleRequest(handle), out count);

    public int GetAvailAxisName(int handle, int axisIndex, out string name)
        => PostString("api/gskrm/get-avail-axis-name", new GskrmAxisRequest(handle, axisIndex), out name);

    public int GetAvailAxisUnits(int handle, int axisIndex, out string unit)
        => PostString("api/gskrm/get-avail-axis-units", new GskrmAxisRequest(handle, axisIndex), out unit);
}
