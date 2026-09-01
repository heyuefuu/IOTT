namespace IndustrialIoT.Protocols.Gsk;

public sealed partial class GskrmIpcClient
{
    public int GetPlcData(int handle, string address, int length, byte[] buffer)
    {
        int rc = PostValue<byte[]>("api/gskrm/get-plc-data",
            new GskrmPlcReadRequest(handle, address, length), out var value);
        if (value is not null)
            Array.Copy(value, buffer, Math.Min(buffer.Length, value.Length));
        return rc;
    }

    public int SetPlcData(int handle, string address, byte[] data)
        => PostCode("api/gskrm/set-plc-data", new GskrmPlcWriteRequest(handle, address, data));
}
