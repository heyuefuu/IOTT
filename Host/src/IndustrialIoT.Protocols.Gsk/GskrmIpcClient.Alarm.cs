namespace IndustrialIoT.Protocols.Gsk;

public sealed partial class GskrmIpcClient
{
    public int GetAlarmCount(int handle, out int count)
        => PostInt("api/gskrm/get-alarm-count", new GskrmHandleRequest(handle), out count);

    public int GetAlarmInfo(int handle, int index, out GskrmAlarm alarm)
    {
        int rc = PostValue<GskrmAlarm>("api/gskrm/get-alarm-info",
            new GskrmIndexedRequest(handle, index), out var value);
        alarm = value ?? new GskrmAlarm { Code = 0, Message = "", Source = "" };
        return rc;
    }
}
