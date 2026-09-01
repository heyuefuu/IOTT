namespace IndustrialIoT.Protocols.HncSdk;

using System.Diagnostics;

public sealed class HncSdkShimProcess : IDisposable
{
    public const string DefaultBaseUrl = "http://127.0.0.1:39210";
    public const string ExecutableName = "IndustrialIoT.HncSdkShim.exe";

    private Process? process;
    private bool disposed;

    private HncSdkShimProcess(Process? process) => this.process = process;

    public static HncSdkShimProcess Start(string baseUrl, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("HNC SDK shim executable path is required.", nameof(executablePath));
        if (!File.Exists(executablePath))
            throw new FileNotFoundException(
                $"HNC SDK shim executable not found at '{executablePath}'. Either set ExtendedProperties[\"ShimPath\"] explicitly or publish {ExecutableName} next to the host (or to one of the conventional locations).",
                executablePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = $"--urls {baseUrl}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        return new HncSdkShimProcess(Process.Start(startInfo));
    }

    /// <summary>
    /// Probe common deployment layouts for the shim executable. Returns null when nothing matches —
    /// callers should fall back to explicit ShimPath or treat the shim as externally hosted.
    /// </summary>
    public static string? ResolveDefaultPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var configurations = new[] { "Debug", "Release" };
        var candidates = new List<string>
        {
            // Flat: shim next to host (typical published layout)
            Path.Combine(baseDir, ExecutableName),
            // Subfolder: ./IndustrialIoT.HncSdkShim/...
            Path.Combine(baseDir, "IndustrialIoT.HncSdkShim", ExecutableName),
        };
        // Dev: sibling project bin output. baseDir is .../<Project>/bin/<Config>/net8.0/
        // so the shim sits four levels up at .../IndustrialIoT.HncSdkShim/bin/<Config>/net8.0/win-x86/.
        foreach (var config in configurations)
            candidates.Add(Path.Combine(baseDir, "..", "..", "..", "..",
                "IndustrialIoT.HncSdkShim", "bin", config, "net8.0", "win-x86", ExecutableName));

        foreach (var candidate in candidates)
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    public bool Owns => process is not null;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var p = process;
        process = null;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.WaitForExit(2000); } catch { }
        try { p.Dispose(); } catch { }
    }
}
