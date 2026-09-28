namespace IndustrialIoT.Protocols.JingDiao;

public static class JingDiaoProgramPath
{
    public static (string FileName, string Directory) Resolve(string? fileName, string? remotePath)
    {
        var path = (remotePath ?? "").Replace('\\', '/');
        var separator = path.LastIndexOf('/');
        var directory = separator < 0 ? "" : path[..(separator == 0 || separator == 2 && path[1] == ':' ? separator + 1 : separator)];
        var name = string.IsNullOrWhiteSpace(path) || path.EndsWith('/')
            ? fileName ?? "" : path[(separator + 1)..];
        Validate(name, directory);
        return (name, directory);
    }

    public static void Validate(string fileName, string directory)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".."
            || fileName.IndexOfAny(['/', '\\', ':', '<', '>', '"', '|', '?', '*']) >= 0
            || fileName.Any(char.IsControl) || fileName.EndsWith('.') || fileName.EndsWith(' '))
            throw new ArgumentException("A plain NC program file name is required.");
        var stem = fileName.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Reserved NC program file name.");
        var normalized = directory.Replace('\\', '/');
        if (normalized.Any(char.IsControl) || normalized.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0
            || normalized.Split('/').Any(part => part is "." or "..")
            || normalized.IndexOf(':') >= 0 && !(normalized.Length >= 3 && char.IsAsciiLetter(normalized[0])
                && normalized[1] == ':' && normalized[2] == '/' && normalized.LastIndexOf(':') == 1))
            throw new ArgumentException("Invalid NC program target directory.");
        if (normalized.Length > 0 && !normalized.StartsWith('/')
            && !(normalized.Length >= 3 && normalized[1] == ':' && normalized[2] == '/'))
            throw new ArgumentException("NC program target directory must be an absolute path.");
    }
}
