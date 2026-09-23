namespace MachineConnectionApi.Services;

using MachineConnectionApi.Models;

public static class CsServerDirectoryBrowser
{
    public static CsServerDirectoryListing Browse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var roots = DriveInfo.GetDrives().Where(drive => drive.IsReady)
                .Select(drive => new CsServerDirectoryEntry(drive.RootDirectory.FullName, drive.RootDirectory.FullName))
                .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToList();
            return new(null, null, roots);
        }

        var directory = NormalizeRoot(path);
        if (!Directory.Exists(directory))
            throw new InvalidOperationException("目录不存在，请选择已有目录");
        var children = new DirectoryInfo(directory).EnumerateDirectories()
            .Where(child => (child.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Select(child => new CsServerDirectoryEntry(child.Name, child.FullName)).ToList();
        return new(directory, Directory.GetParent(directory)?.FullName, children);
    }

    public static string NormalizeRoot(string path)
    {
        path = path.Trim();
        if (!Path.IsPathFullyQualified(path) || OperatingSystem.IsWindows() && path.StartsWith(@"\\"))
            throw new InvalidOperationException("本地存储目录必须是本机磁盘的绝对路径");
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows() && fullPath.StartsWith(@"\\"))
            throw new InvalidOperationException("本地存储目录必须是本机磁盘的绝对路径");
        EnsureNoLinks(fullPath);
        return fullPath;
    }

    internal static void EnsureNoLinks(string path)
    {
        for (string? current = path; current is not null;
             current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current)))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("存储路径不支持符号链接或目录联接");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
