namespace IndustrialIoT.Protocols.Gsk;

using System.Runtime.InteropServices;
using System.Text;

public sealed partial class NativeGskrmApi
{
    [DllImport(DllName, EntryPoint = "GSKRM_GetCNCFileCount", CallingConvention = Conv)]
    private static extern int GSKRM_GetCNCFileCount(IntPtr handle, out uint count);

    // Returns a newline-delimited list packed into `buffer` (length `bufferLen`) —
    // the actual ABI may be an array of fixed-size records; we choose a conservative
    // line-based form until the SDK header lands.
    [DllImport(DllName, EntryPoint = "GSKRM_GetCNCFileList", CallingConvention = Conv, CharSet = CharSet.Ansi)]
    private static extern int GSKRM_GetCNCFileList(IntPtr handle, StringBuilder buffer, uint bufferLen, out uint nameCount);

    [DllImport(DllName, EntryPoint = "GSKRM_GetCNCFileInfo", CallingConvention = Conv, CharSet = CharSet.Ansi)]
    private static extern int GSKRM_GetCNCFileInfo(IntPtr handle,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        out uint sizeBytes, out long modifiedSecondsSinceEpoch);

    [DllImport(DllName, EntryPoint = "GSKRM_ReceiveCNCFile", CallingConvention = Conv, CharSet = CharSet.Ansi)]
    private static extern int GSKRM_ReceiveCNCFile(IntPtr handle,
        [MarshalAs(UnmanagedType.LPStr)] string remoteName,
        [MarshalAs(UnmanagedType.LPStr)] string localPath,
        IntPtr progress);

    [DllImport(DllName, EntryPoint = "GSKRM_SendCNCFile", CallingConvention = Conv, CharSet = CharSet.Ansi)]
    private static extern int GSKRM_SendCNCFile(IntPtr handle,
        [MarshalAs(UnmanagedType.LPStr)] string localPath,
        [MarshalAs(UnmanagedType.LPStr)] string remoteName,
        IntPtr progress);

    [DllImport(DllName, EntryPoint = "GSKRM_DeleteCNCFile", CallingConvention = Conv, CharSet = CharSet.Ansi)]
    private static extern int GSKRM_DeleteCNCFile(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string remoteName);

    [DllImport(DllName, EntryPoint = "GSKRM_Prog_Install", CallingConvention = Conv, CharSet = CharSet.Ansi)]
    private static extern int GSKRM_Prog_Install(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string remoteName);

    [DllImport(DllName, EntryPoint = "GSKRM_Prog_Uninstall", CallingConvention = Conv, CharSet = CharSet.Ansi)]
    private static extern int GSKRM_Prog_Uninstall(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string remoteName);

    [DllImport(DllName, EntryPoint = "GSKRM_GetPartCount", CallingConvention = Conv)]
    private static extern int GSKRM_GetPartCount(IntPtr handle, out uint count);

    [DllImport(DllName, EntryPoint = "GSKRM_GetCutTime", CallingConvention = Conv)]
    private static extern int GSKRM_GetCutTime(IntPtr handle, out uint secondsElapsed);

    [DllImport(DllName, EntryPoint = "GSKRM_GetRunTime", CallingConvention = Conv)]
    private static extern int GSKRM_GetRunTime(IntPtr handle, out uint secondsElapsed);

    public int GetCNCFileCount(int handle, out int count)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero) { count = 0; return GskrmErrorCodes.InvalidHandle; }
        int rc = GSKRM_GetCNCFileCount(native, out uint value);
        count = checked((int)value);
        return rc;
    }

    public int GetCNCFileList(int handle, out IReadOnlyList<GskrmCncFileEntry> entries)
    {
        var buffer = new StringBuilder(64 * 1024);
        var native = Resolve(handle);
        if (native == IntPtr.Zero) { entries = []; return GskrmErrorCodes.InvalidHandle; }
        int rc = GSKRM_GetCNCFileList(native, buffer, (uint)buffer.Capacity, out _);
        if (rc != GskrmErrorCodes.Ok) { entries = []; return rc; }

        var list = new List<GskrmCncFileEntry>();
        foreach (var line in buffer.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            // Line shape: "<name>\t<size>\t<mtime-unix>\t<attr>" — best-guess parse;
            // swap for a binary record layout once the SDK header is available.
            var parts = line.Split('\t');
            list.Add(new GskrmCncFileEntry
            {
                Name = parts[0],
                SizeBytes = parts.Length > 1 && long.TryParse(parts[1], out var s) ? s : 0,
                ModifiedAt = parts.Length > 2 && long.TryParse(parts[2], out var t) && t > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(t) : null,
                Attribute = parts.Length > 3 ? parts[3] : null
            });
        }
        entries = list;
        return rc;
    }

    public int GetCNCFileInfo(int handle, string name, out GskrmCncFileEntry entry)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero)
        {
            entry = new GskrmCncFileEntry { Name = name };
            return GskrmErrorCodes.InvalidHandle;
        }
        int rc = GSKRM_GetCNCFileInfo(native, name, out uint size, out long mtime);
        entry = new GskrmCncFileEntry
        {
            Name = name,
            SizeBytes = size,
            ModifiedAt = mtime > 0 ? DateTimeOffset.FromUnixTimeSeconds(mtime) : null,
            Attribute = null
        };
        return rc;
    }

    public int ReceiveCNCFile(int handle, string remoteName, string localPath, IProgress<long>? progress = null)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero)
            return GskrmErrorCodes.InvalidHandle;
        int rc = GSKRM_ReceiveCNCFile(native, remoteName, localPath, IntPtr.Zero);
        // Progress callback is not part of the native signature — report once at the
        // end. A finer-grained variant (if the SDK exposes a chunked receive) can hook
        // in here later.
        if (rc == GskrmErrorCodes.Ok && File.Exists(localPath))
            progress?.Report(new FileInfo(localPath).Length);
        return rc;
    }

    public int SendCNCFile(int handle, string localPath, string remoteName, IProgress<long>? progress = null)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero)
            return GskrmErrorCodes.InvalidHandle;
        int rc = GSKRM_SendCNCFile(native, localPath, remoteName, IntPtr.Zero);
        if (rc == GskrmErrorCodes.Ok && File.Exists(localPath))
            progress?.Report(new FileInfo(localPath).Length);
        return rc;
    }

    public int DeleteCNCFile(int handle, string remoteName)
        => WithNative(handle, native => GSKRM_DeleteCNCFile(native, remoteName));
    public int ProgInstall(int handle, string remoteName)
        => WithNative(handle, native => GSKRM_Prog_Install(native, remoteName));
    public int ProgUninstall(int handle, string remoteName)
        => WithNative(handle, native => GSKRM_Prog_Uninstall(native, remoteName));

    public int GetPartCount(int handle, out int count)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero) { count = 0; return GskrmErrorCodes.InvalidHandle; }
        int rc = GSKRM_GetPartCount(native, out uint value);
        count = checked((int)value);
        return rc;
    }

    public int GetCutTime(int handle, out TimeSpan elapsed)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero) { elapsed = TimeSpan.Zero; return GskrmErrorCodes.InvalidHandle; }
        int rc = GSKRM_GetCutTime(native, out uint seconds);
        elapsed = TimeSpan.FromSeconds(seconds);
        return rc;
    }

    public int GetRunTime(int handle, out TimeSpan elapsed)
    {
        var native = Resolve(handle);
        if (native == IntPtr.Zero) { elapsed = TimeSpan.Zero; return GskrmErrorCodes.InvalidHandle; }
        int rc = GSKRM_GetRunTime(native, out uint seconds);
        elapsed = TimeSpan.FromSeconds(seconds);
        return rc;
    }
}
