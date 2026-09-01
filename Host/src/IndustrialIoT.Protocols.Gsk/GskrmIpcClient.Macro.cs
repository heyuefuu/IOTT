namespace IndustrialIoT.Protocols.Gsk;

public sealed partial class GskrmIpcClient
{
    public int GetMacroValue(int handle, int number, out double value)
    {
        int rc = PostValue<double>("api/gskrm/get-macro-value",
            new GskrmNumberRequest(handle, number), out var result);
        value = result;
        return rc;
    }

    public int SetMacroValue(int handle, int number, double value)
        => PostCode("api/gskrm/set-macro-value", new GskrmMacroWriteRequest(handle, number, value));
}
