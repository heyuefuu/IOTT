namespace MachineConnectionApi.Services;

using System.Text.RegularExpressions;

public sealed partial class CsConnectivityService
{
    private const string FtpTemporaryPrefix = ".iott-ftp-";
    private sealed record FtpDirectoryEntry(string Name, bool IsDirectory, long Size, DateTime ModifiedUtc);

    private static string NormalizeFtpPath(string path, string currentDirectory)
    {
        path = path.Replace('\\', '/');
        var parts = path.StartsWith('/') ? new List<string>()
            : currentDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new InvalidOperationException("Path is outside the FTP root");
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            if (part.Contains(':') || part.Any(char.IsControl) || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                part.TrimEnd('.', ' ') != part || part.StartsWith(FtpTemporaryPrefix, StringComparison.OrdinalIgnoreCase) ||
                OperatingSystem.IsWindows() && Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("Invalid FTP path");
            parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }

    private static string ResolveFtpDiskPath(ServerRuntime runtime, string virtualPath)
    {
        var root = runtime.RootDirectory ?? throw new InvalidOperationException("Local storage is not configured");
        var fullPath = Path.GetFullPath(Path.Combine(root, virtualPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Path is outside the FTP root");
        CsServerDirectoryBrowser.EnsureNoLinks(fullPath);
        return fullPath;
    }

    private static string ChangeFtpDirectory(ServerRuntime runtime, string path, string currentDirectory)
    {
        var normalized = NormalizeFtpPath(path, currentDirectory);
        if (runtime.RootDirectory is null) return "/";
        if (!Directory.Exists(ResolveFtpDiskPath(runtime, normalized)))
            throw new DirectoryNotFoundException("FTP directory does not exist");
        return normalized;
    }

    private static IReadOnlyList<FtpDirectoryEntry> ListFtpEntries(ServerRuntime runtime, string path)
    {
        if (runtime.RootDirectory is null)
            return runtime.FtpFiles.Select(entry => new FtpDirectoryEntry(entry.Key, false, entry.Value.LongLength, DateTime.UtcNow)).ToList();
        var directory = ResolveFtpDiskPath(runtime, path);
        var entries = File.Exists(directory) ? new FileSystemInfo[] { new FileInfo(directory) }
            : new DirectoryInfo(directory).EnumerateFileSystemInfos();
        var result = new List<FtpDirectoryEntry>();
        foreach (var entry in entries)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.Name.StartsWith(FtpTemporaryPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            try { _ = NormalizeFtpPath(entry.Name, "/"); }
            catch (InvalidOperationException) { continue; }
            result.Add(new(entry.Name, entry is DirectoryInfo, entry is FileInfo file ? file.Length : 0, entry.LastWriteTimeUtc));
        }
        return result.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static long GetFtpFileSize(ServerRuntime runtime, string path)
    {
        if (runtime.RootDirectory is null)
            return runtime.FtpFiles.TryGetValue(NormalizeFtpName(path), out var content) ? content.LongLength : -1;
        var file = new FileInfo(ResolveFtpDiskPath(runtime, path));
        return file.Exists ? file.Length : -1;
    }

    private void StoreFtpDiskFile(ServerRuntime runtime, string path, byte[] content)
    {
        lock (_ftpStorageGate)
        {
            runtime.Cts.Token.ThrowIfCancellationRequested();
            var target = ResolveFtpDiskPath(runtime, path);
            if (Directory.Exists(target)) throw new IOException("The target is a directory");
            var temporary = Path.Combine(Path.GetDirectoryName(target)!, FtpTemporaryPrefix + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllBytes(temporary, content);
                runtime.Cts.Token.ThrowIfCancellationRequested();
                CsServerDirectoryBrowser.EnsureNoLinks(target);
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private static string CreateFtpDirectory(ServerRuntime runtime, string path, string currentDirectory)
    {
        var normalized = NormalizeFtpPath(path, currentDirectory);
        Directory.CreateDirectory(ResolveFtpDiskPath(runtime, normalized));
        return normalized;
    }

    private static void RemoveFtpDirectory(ServerRuntime runtime, string path, string currentDirectory)
    {
        var normalized = NormalizeFtpPath(path, currentDirectory);
        if (normalized == "/") throw new InvalidOperationException("The FTP root cannot be removed");
        Directory.Delete(ResolveFtpDiskPath(runtime, normalized));
    }

    private static async Task WriteFtpDataAsync(Stream stream, Stream content, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FtpDataTransferTimeout);
        await content.CopyToAsync(stream, timeout.Token);
    }

    private static Stream? OpenFtpDownload(ServerRuntime runtime, string path)
    {
        if (runtime.RootDirectory is null)
            return runtime.FtpFiles.TryGetValue(NormalizeFtpName(path), out var content) ? new MemoryStream(content, writable: false) : null;
        var fullPath = ResolveFtpDiskPath(runtime, path);
        return File.Exists(fullPath)
            ? new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true) : null;
    }
}
