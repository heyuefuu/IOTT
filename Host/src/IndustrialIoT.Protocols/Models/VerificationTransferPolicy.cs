namespace IndustrialIoT.Protocols.Models;

/// <summary>Strict verification-only path policy; ordinary program uploads retain their semantics.</summary>
public static class VerificationTransferPolicy
{
    public const long MaximumFileBytes = 200L * 1024 * 1024;
    public const long MaximumRequestBytes = MaximumFileBytes + 1024 * 1024;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".nc", ".ncg", ".txt", ".xml", ".prg" };

    public static bool IsFileName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || !name.StartsWith("verify-", StringComparison.Ordinal)) return false;
        var extension = Path.GetExtension(name);
        return Extensions.Contains(extension) && name.Length == 7 + 32 + extension.Length
            && Guid.TryParseExact(name.AsSpan(7, 32), "N", out _);
    }

    public static bool IsDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.Length > 800 || directory != directory.Trim() || directory.Contains("//", StringComparison.Ordinal)
            || directory.Any(c => char.IsControl(c) || c is '\\' or ':' or '"' or '*' or '?' or '<' or '>' or '|' or '%')) return false;
        return !directory.Split('/').Any(part => part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.'));
    }

    public static void Validate(string directory, string fileName)
    {
        if (!IsDirectory(directory) || !IsFileName(fileName))
            throw new ArgumentException("Only a validated directory and verify-<32 hex digits> test filename are allowed.");
    }
}
