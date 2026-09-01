namespace IndustrialIoT.Protocols.Gsk;

using System.Runtime.InteropServices;
using System.Collections.Concurrent;

/// <summary>
/// Real <c>gskrm.dll</c> P/Invoke wrapper implementing <see cref="IGskrmApi"/>.
///
/// IMPORTANT
///   - The DLL shipped at repo root is x86 (`PE32`). This class loads via
///     `DllImport("gskrm.dll")`, so the host **process bitness must match**
///     the DLL. For AnyCPU/x64 hosts, drop an x64 build of the SDK into
///     `native/` before running.
///   - GSK RM SDK exports use the `GSKRM_` prefix, no `@N` decoration, so
///     <see cref="CallingConvention.Cdecl"/> is assumed. Re-verify against
///     the official header when it lands.
///   - Every signature here is [unverified] — the header is not checked in.
///     Marshalling (especially struct layout and array sizing) WILL need
///     touch-up once a real CNC is available to probe against.
///
/// The full surface is split across partial files:
///   NativeGskrmApi.Connection.cs  / NativeGskrmApi.System.cs
///   NativeGskrmApi.Status.cs      / NativeGskrmApi.Position.cs
///   NativeGskrmApi.Rate.cs        / NativeGskrmApi.Alarm.cs
///   NativeGskrmApi.Plc.cs         / NativeGskrmApi.Macro.cs
///   NativeGskrmApi.Param.cs       / NativeGskrmApi.Tool.cs
///   NativeGskrmApi.Program.cs     / NativeGskrmApi.Advanced.cs
/// </summary>
public sealed partial class NativeGskrmApi : IGskrmApi
{
    internal const string DllName = "gskrm.dll";
    internal const CallingConvention Conv = CallingConvention.Cdecl;
    internal const int DefaultStringBuffer = 260;

    private readonly ConcurrentDictionary<int, IntPtr> handles = new();
    private int nextHandle;

    public NativeGskrmApi() { }

    private static string ReadCString(byte[] buffer)
    {
        int len = Array.IndexOf<byte>(buffer, 0);
        return System.Text.Encoding.ASCII.GetString(buffer, 0, len < 0 ? buffer.Length : len);
    }

    private static bool TryParseIpBytes(string host, byte[] buffer)
    {
        var parts = host.Split('.');
        if (parts.Length != 4)
            return false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!byte.TryParse(parts[i], out buffer[i]))
                return false;
        }
        return true;
    }

    private IntPtr Resolve(int handle)
        => handles.TryGetValue(handle, out var native) ? native : IntPtr.Zero;

    private int WithNative(int handle, Func<IntPtr, int> action)
    {
        var native = Resolve(handle);
        return native == IntPtr.Zero ? GskrmErrorCodes.InvalidHandle : action(native);
    }
}
