namespace IndustrialIoT.JingDiaoShim;

using System.Collections.Concurrent;
using IndustrialIoT.Protocols.JingDiao;

public sealed class JingDiaoSessionStore : IDisposable
{
    private readonly ConcurrentDictionary<string, IntPtr> sessions = [];
    private readonly IJdMonApi api;

    public JingDiaoSessionStore(IJdMonApi api) => this.api = api;

    public string Add(IntPtr handle)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        sessions[sessionId] = handle;
        return sessionId;
    }

    public bool TryGet(string sessionId, out IntPtr handle) => sessions.TryGetValue(sessionId, out handle);

    public bool Remove(string sessionId, out IntPtr handle) => sessions.TryRemove(sessionId, out handle);

    public void Dispose()
    {
        foreach (var pair in sessions)
        {
            var handle = pair.Value;
            try { api.Disconnect(handle); } catch { }
            try { api.Delete(ref handle); } catch { }
        }
        sessions.Clear();
    }
}
