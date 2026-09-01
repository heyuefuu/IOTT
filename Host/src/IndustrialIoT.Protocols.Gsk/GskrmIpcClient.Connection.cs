namespace IndustrialIoT.Protocols.Gsk;

public sealed partial class GskrmIpcClient
{
    public int CloseInstance(int handle)
        => PostCode("api/gskrm/close-instance", new GskrmHandleRequest(handle));

    public int GetConnectState(int handle, out bool connected)
    {
        int rc = PostValue<bool>("api/gskrm/get-connect-state", new GskrmHandleRequest(handle), out var value);
        connected = value;
        return rc;
    }

    public int SetOvertime(int handle, int timeoutMs)
        => PostCode("api/gskrm/set-overtime", new GskrmTimeoutRequest(handle, timeoutMs));
}
