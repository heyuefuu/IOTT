namespace IndustrialIoT.Protocols.Gsk;

public sealed partial class GskrmIpcClient
{
    public int GetFeedSpeedAct(int handle, out int feedMmPerMin)
        => PostInt("api/gskrm/get-feed-speed-act", new GskrmHandleRequest(handle), out feedMmPerMin);

    public int GetFeedSpeedProg(int handle, out int feedMmPerMin)
        => PostInt("api/gskrm/get-feed-speed-prog", new GskrmHandleRequest(handle), out feedMmPerMin);

    public int GetSpindleSpeedAct(int handle, out int rpm)
        => PostInt("api/gskrm/get-spindle-speed-act", new GskrmHandleRequest(handle), out rpm);

    public int GetSpindleSpeedProg(int handle, out int rpm)
        => PostInt("api/gskrm/get-spindle-speed-prog", new GskrmHandleRequest(handle), out rpm);

    public int GetAllRateInfo(int handle, out GskrmRateInfo rates)
    {
        int rc = PostValue<GskrmRateInfo>("api/gskrm/get-all-rate-info", new GskrmHandleRequest(handle), out var value);
        rates = value ?? new GskrmRateInfo { FeedRate = 0, FastRate = 0, JogRate = 0, SpindleRate = 0, HandWheelRate = 0 };
        return rc;
    }
}
