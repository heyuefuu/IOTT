namespace IndustrialIoT.Protocols.Gsk;

using IndustrialIoT.Domain.ValueObjects;

internal sealed record GskWebServerOptions
{
    public required Uri BaseUri { get; init; }
    public required Uri ManagementBaseUri { get; init; }
    public required string HealthPath { get; init; }
    public required string RealtimeDataPath { get; init; }
    public required string StaticDataPath { get; init; }
    public required string MacroReadPath { get; init; }
    public required string MacroWritePath { get; init; }
    public required string ParamReadPath { get; init; }
    public required string ParamWritePath { get; init; }
    public required string DiagnoseReadPath { get; init; }
    public required string ProgramListPath { get; init; }
    public required string ProgramLoadPath { get; init; }
    public required string ProgramUploadPath { get; init; }
    public required string ProgramDownloadPath { get; init; }
    public required string SoftPlcReadPath { get; init; }
    public required string SoftPlcWritePath { get; init; }
    public required string ToolOffsetReadPath { get; init; }
    public required string ToolOffsetWritePath { get; init; }
    public required string ToolLifeReadPath { get; init; }
    public required string AlarmReadPath { get; init; }
    public required string HistoryReadPath { get; init; }
    public required string WorkshopPath { get; init; }
    public string? WorkshopAuthToken { get; init; }
    public required string RealtimeWebSocketBaseUri { get; init; }
    public required string RealtimeWebSocketPath { get; init; }

    public static GskWebServerOptions From(DeviceConnectionConfig config)
    {
        var props = config.ExtendedProperties;
        var scheme = Get(props, "Scheme", "http");
        var defaultPort = scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 11520;
        var port = config.Port > 0 ? config.Port : defaultPort;
        var baseUrl = Get(props, "BaseUrl", $"{scheme}://{config.Host}:{port}");
        var managementUrl = Get(props, "ManagementBaseUrl", $"{scheme}://{config.Host}:3000");
        var deviceSn = Get(props, "DeviceSn", "cnc").Trim('/');
        var root = Get(props, "CncRootPath", $"/api/v1/{deviceSn}").TrimEnd('/');
        var wsScheme = scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var wsBase = Get(props, "RealtimeWebSocketBaseUrl", $"{wsScheme}://{config.Host}:{port}");

        return new()
        {
            BaseUri = new Uri(baseUrl.TrimEnd('/') + "/"),
            ManagementBaseUri = new Uri(managementUrl.TrimEnd('/') + "/"),
            HealthPath = Get(props, "HealthPath", $"{root}/mc"),
            RealtimeDataPath = Get(props, "RealtimeDataPath", $"{root}/mc"),
            StaticDataPath = Get(props, "StaticDataPath", $"{root}/mc"),
            MacroReadPath = Get(props, "MacroReadPath", $"{root}/macro-values"),
            MacroWritePath = Get(props, "MacroWritePath", $"{root}/macro-values"),
            ParamReadPath = Get(props, "ParamReadPath", $"{root}/params"),
            ParamWritePath = Get(props, "ParamWritePath", $"{root}/params"),
            DiagnoseReadPath = Get(props, "DiagnoseReadPath", $"{root}/info/diags"),
            ProgramListPath = Get(props, "ProgramListPath", $"{root}/mc/gcode"),
            ProgramLoadPath = Get(props, "ProgramLoadPath", $"{root}/mc/gcode"),
            ProgramUploadPath = Get(props, "ProgramUploadPath", $"{root}/file"),
            ProgramDownloadPath = Get(props, "ProgramDownloadPath", $"{root}/file"),
            SoftPlcReadPath = Get(props, "SoftPlcReadPath", $"{root}/plc/regs"),
            SoftPlcWritePath = Get(props, "SoftPlcWritePath", $"{root}/plc/regs"),
            ToolOffsetReadPath = Get(props, "ToolOffsetReadPath", $"{root}/tool-offsets"),
            ToolOffsetWritePath = Get(props, "ToolOffsetWritePath", $"{root}/tool-offsets"),
            ToolLifeReadPath = Get(props, "ToolLifeReadPath", $"{root}/tool-lifes"),
            AlarmReadPath = Get(props, "AlarmReadPath", $"{root}/info/alarms"),
            HistoryReadPath = Get(props, "HistoryReadPath", $"{root}/info/history"),
            WorkshopPath = Get(props, "WorkshopPath", "/api/v1/workshop"),
            WorkshopAuthToken = GetOptional(props, "WorkshopAuthToken") ?? GetOptional(props, "AuthToken"),
            RealtimeWebSocketBaseUri = wsBase,
            RealtimeWebSocketPath = Get(props, "RealtimeWebSocketPath", $"/ws/{deviceSn}")
        };
    }

    private static string Get(IReadOnlyDictionary<string, string> props, string key, string fallback) =>
        props.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static string? GetOptional(IReadOnlyDictionary<string, string> props, string key) =>
        props.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
