namespace IndustrialIoT.Protocols.JingDiao;

using IndustrialIoT.Domain.ValueObjects;

public sealed record JingDiaoOptions
{
    public Uri ShimBaseUri { get; init; } = new(JingDiaoShimProcess.DefaultBaseUrl);
    public bool AutoStartShim { get; init; } = true;
    public string? ShimPath { get; init; }
    public int RpcPort { get; init; } = 89;
    public int CallbackPort { get; init; } = 7080;
    public int FileUploadPort { get; init; } = 7081;
    public int FileDownloadPort { get; init; } = 7082;
    public int TimeoutMs { get; init; } = 10000;
    public bool UploadAddToTask { get; init; }
    public bool UploadSetMainProgram { get; init; }
    public int FileListBufferSize { get; init; } = 102400;

    public static JingDiaoOptions From(DeviceConnectionConfig config)
    {
        var ext = config.ExtendedProperties;
        return new JingDiaoOptions
        {
            ShimBaseUri = new Uri(ext.GetValueOrDefault("ShimBaseUrl") ?? JingDiaoShimProcess.DefaultBaseUrl),
            AutoStartShim = !ext.TryGetValue("AutoStartShim", out var autoStart)
                || !autoStart.Equals("false", StringComparison.OrdinalIgnoreCase),
            ShimPath = ext.GetValueOrDefault("ShimPath"),
            RpcPort = config.Port > 0 ? config.Port : Int(ext, "RpcPort", 89),
            CallbackPort = Int(ext, "CallbackPort", 7080),
            FileUploadPort = Int(ext, "FileUploadPort", 7081),
            FileDownloadPort = Int(ext, "FileDownloadPort", 7082),
            TimeoutMs = (int)(config.ConnectTimeout > TimeSpan.Zero
                ? config.ConnectTimeout.TotalMilliseconds
                : TimeSpan.FromSeconds(10).TotalMilliseconds),
            UploadAddToTask = Bool(ext, "UploadAddToTask", false),
            UploadSetMainProgram = Bool(ext, "UploadSetMainProgram", false),
            FileListBufferSize = Int(ext, "FileListBufferSize", 102400),
        };
    }

    private static int Int(IReadOnlyDictionary<string, string> ext, string key, int fallback)
        => ext.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    private static bool Bool(IReadOnlyDictionary<string, string> ext, string key, bool fallback)
        => ext.TryGetValue(key, out var value) ? bool.TryParse(value, out var parsed) && parsed : fallback;
}
