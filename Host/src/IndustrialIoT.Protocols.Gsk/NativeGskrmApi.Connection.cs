namespace IndustrialIoT.Protocols.Gsk;

using System.Runtime.InteropServices;
using System.Text;

public sealed partial class NativeGskrmApi
{
    // ── P/Invoke ─────────────────────────────────────────────────────
    [DllImport(DllName, EntryPoint = "GSKRM_CreateInstance", CallingConvention = Conv)]
    private static extern IntPtr GSKRM_CreateInstance(byte[] cncIPAddr, int type);

    [DllImport(DllName, EntryPoint = "GSKRM_CloseInstance", CallingConvention = Conv)]
    private static extern void GSKRM_CloseInstance(IntPtr handle);

    [DllImport(DllName, EntryPoint = "GSKRM_SetOvertime", CallingConvention = Conv)]
    private static extern int GSKRM_SetOvertime(IntPtr handle, uint timeoutMs);

    // ── IGskrmApi ────────────────────────────────────────────────────
    public int CreateInstance(string host, int port, int timeoutMs, out int handle)
    {
        handle = 0;
        var ip = new byte[4];
        if (!TryParseIpBytes(host, ip))
            return GskrmErrorCodes.InvalidArgument;
        IntPtr native = GSKRM_CreateInstance(ip, 1);
        if (native == IntPtr.Zero)
            return GskrmErrorCodes.IoError;
        handle = Interlocked.Increment(ref nextHandle);
        handles[handle] = native;
        if (timeoutMs > 0)
            return SetOvertime(handle, timeoutMs);
        return GskrmErrorCodes.Ok;
    }

    public int CloseInstance(int handle)
    {
        if (!handles.TryRemove(handle, out var native))
            return GskrmErrorCodes.InvalidHandle;
        GSKRM_CloseInstance(native);
        return GskrmErrorCodes.Ok;
    }

    public int GetConnectState(int handle, out bool connected)
    {
        connected = Resolve(handle) != IntPtr.Zero;
        return connected ? GskrmErrorCodes.Ok : GskrmErrorCodes.InvalidHandle;
    }

    public int SetOvertime(int handle, int timeoutMs)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero)
            return GskrmErrorCodes.InvalidHandle;
        return GSKRM_SetOvertime(native, (uint)Math.Max(0, timeoutMs));
    }
}
