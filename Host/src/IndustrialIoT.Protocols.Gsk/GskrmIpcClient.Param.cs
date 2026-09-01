namespace IndustrialIoT.Protocols.Gsk;

public sealed partial class GskrmIpcClient
{
    public int GetParamValue(int handle, int number, int axis, out int value)
        => PostInt("api/gskrm/get-param-value", new GskrmParamRequest(handle, number, axis), out value);

    public int SetParamValue(int handle, int number, int axis, int value)
        => PostCode("api/gskrm/set-param-value", new GskrmParamWriteRequest(handle, number, axis, value));
}
