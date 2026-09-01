namespace IndustrialIoT.Protocols.Gsk;

public sealed partial class GskrmIpcClient
{
    public int GetCncState(int handle, out GskrmCncState state)
    {
        int rc = PostValue<GskrmCncState>("api/gskrm/get-cnc-state", new GskrmHandleRequest(handle), out var value);
        state = value ?? new GskrmCncState { RawState = 0 };
        return rc;
    }

    public int GetStatus(int handle, out int statusCode)
        => PostInt("api/gskrm/get-status", new GskrmHandleRequest(handle), out statusCode);

    public int GetWorkMode(int handle, out string mode)
        => PostString("api/gskrm/get-work-mode", new GskrmHandleRequest(handle), out mode);

    public int GetRunCncProgName(int handle, out string programName)
        => PostString("api/gskrm/get-run-cnc-prog-name", new GskrmHandleRequest(handle), out programName);

    public int GetMainCncProgName(int handle, out string programName)
        => PostString("api/gskrm/get-main-cnc-prog-name", new GskrmHandleRequest(handle), out programName);

    public int GetRunLineNo(int handle, out int lineNo)
        => PostInt("api/gskrm/get-run-line-no", new GskrmHandleRequest(handle), out lineNo);

    public int GetEspState(int handle, out bool estop)
    {
        int rc = PostValue<bool>("api/gskrm/get-esp-state", new GskrmHandleRequest(handle), out var value);
        estop = value;
        return rc;
    }
}
