namespace IndustrialIoT.Protocols.FileTransfer;

public static class FileTransferRemotePath
{
    public static string CombineFtpDirectory(string? remoteDirectory, string fileName)
    {
        var directory = (remoteDirectory ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        return string.IsNullOrEmpty(directory) ? $"/{fileName}" : $"{directory}/{fileName}";
    }

    public static string NormalizeSmbPath(string? path)
    {
        return (path ?? string.Empty).Replace('/', '\\').TrimStart('\\');
    }

    public static string CombineSmbDirectory(string? remoteDirectory, string fileName)
    {
        var directory = NormalizeSmbPath(remoteDirectory).TrimEnd('\\');
        return string.IsNullOrEmpty(directory) ? fileName : $@"{directory}\{fileName}";
    }
}
