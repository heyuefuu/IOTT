namespace IndustrialIoT.Protocols.JingDiao;

using System.Diagnostics;

public sealed class JingDiaoShimProcess : IDisposable
{
    public const string DefaultBaseUrl = "http://127.0.0.1:39125";
    public const string ExecutableName = "IndustrialIoT.JingDiaoShim.exe";

    private Process? process;
    private bool disposed;

    private JingDiaoShimProcess(Process? process) => this.process = process;

    public static JingDiaoShimProcess Start(string baseUrl, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("JingDiao shim executable path is required.", nameof(executablePath));
        if (!File.Exists(executablePath))
            throw new FileNotFoundException(
                $"JingDiao shim executable not found at '{executablePath}'. Set ExtendedProperties[\"ShimPath\"] or publish {ExecutableName} next to the host.",
                executablePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = $"--urls {baseUrl}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        return new JingDiaoShimProcess(Process.Start(startInfo));
    }

    public static string? ResolveDefaultPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, ExecutableName),
            Path.Combine(baseDir, "IndustrialIoT.JingDiaoShim", ExecutableName),
        };
        foreach (var config in new[] { "Debug", "Release" })
            candidates.Add(Path.Combine(baseDir, "..", "..", "..", "..",
                "IndustrialIoT.JingDiaoShim", "bin", config, "net8.0", "win-x86", ExecutableName));

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

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var current = process;
        process = null;
        if (current is null) return;
        try { if (!current.HasExited) current.Kill(entireProcessTree: true); } catch { }
        try { current.WaitForExit(2000); } catch { }
        try { current.Dispose(); } catch { }
    }
}
