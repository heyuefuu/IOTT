namespace IndustrialIoT.Security;

using System.Security.Cryptography;
using System.Text;

public static class ServiceTrustKey
{
    public const string Header = "X-IoT-Service-Key";
    private static readonly object Gate = new();

    public static string Load(string? configuredKey = null)
    {
        var supplied = string.IsNullOrWhiteSpace(configuredKey)
            ? Environment.GetEnvironmentVariable("IOTT_SERVICE_KEY") : configuredKey;
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            if (Encoding.UTF8.GetByteCount(supplied) < 32)
                throw new InvalidOperationException("IOTT_SERVICE_KEY must contain at least 32 UTF-8 bytes.");
            return supplied;
        }
        var path = Environment.GetEnvironmentVariable("IOTT_SERVICE_KEY_FILE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IndustrialIoT", "service.key");
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var bytes = Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
                stream.Write(bytes);
                stream.Flush(true);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (IOException) when (File.Exists(path)) { }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var value = File.ReadAllText(path).Trim();
                    if (value.Length < 32) throw new InvalidDataException("Invalid service trust key file.");
                    return value;
                }
                catch (IOException) when (attempt < 3) { Thread.Sleep(20); }
            }
        }
    }

    public static bool Matches(string expected, string? supplied) => !string.IsNullOrEmpty(supplied)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied));
}
