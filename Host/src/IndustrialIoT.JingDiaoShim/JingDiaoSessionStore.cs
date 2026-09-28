namespace IndustrialIoT.JingDiaoShim;

using System.Collections.Concurrent;
using IndustrialIoT.Protocols.JingDiao;

public sealed class JingDiaoSessionStore : IDisposable
{
    private readonly ConcurrentDictionary<string, Session> sessions = [];
    private sealed class Session(IntPtr handle)
    {
        public IntPtr Handle = handle;
    }
    private readonly IJdMonApi api;

    public JingDiaoSessionStore(IJdMonApi api) => this.api = api;

    public string Add(IntPtr handle)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        sessions[sessionId] = new Session(handle);
        return sessionId;
    }

    public bool TryUse<T>(string sessionId, Func<IntPtr, T> action, out T result)
    {
        result = default!;
        if (!sessions.TryGetValue(sessionId, out var session)) return false;
        lock (session)
        {
            if (session.Handle == IntPtr.Zero) return false;
            result = action(session.Handle);
            return true;
        }
    }

    public void Dispose()
    {
        foreach (var sessionId in sessions.Keys)
        {
            try { Close(sessionId); } catch { }
        }
    }

    public JingDiaoIpcResult Close(string sessionId)
    {
        if (!sessions.TryRemove(sessionId, out var session))
            return new() { ReturnCode = -404, ErrorMessage = "Unknown session." };
        lock (session)
        {
            try
            {
                var ok = api.Disconnect(session.Handle);
                var error = ok ? 0 : api.GetLastError(session.Handle);
                return new() { ReturnCode = ok ? 0 : (int)(error == 0 ? 1 : error),
                    ErrorMessage = ok ? null : $"NcMonIO disconnect failed: {error}" };
            }
            finally
            {
                var handle = session.Handle;
                session.Handle = IntPtr.Zero;
                api.Delete(ref handle);
            }
        }
    }
}
