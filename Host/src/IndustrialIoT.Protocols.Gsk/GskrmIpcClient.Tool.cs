namespace IndustrialIoT.Protocols.Gsk;

public sealed partial class GskrmIpcClient
{
    public int GetToolOffsetCount(int handle, out int count)
        => PostInt("api/gskrm/get-tool-offset-count", new GskrmHandleRequest(handle), out count);

    public int GetToolOffsetValue(int handle, int index, out double value)
    {
        int rc = PostValue<double>("api/gskrm/get-tool-offset-value",
            new GskrmIndexedRequest(handle, index), out var result);
        value = result;
        return rc;
    }
}
