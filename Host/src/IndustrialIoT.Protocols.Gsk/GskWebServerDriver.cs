namespace IndustrialIoT.Protocols.Gsk;

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

[ProtocolDriver(ProtocolType.GskWebServer, "GSK", "G3IOT", "GSK-WebServer")]
public sealed partial class GskWebServerDriver :
    IProtocolDriver, IAddressSpaceBrowser, IProgramFileBrowser, INCProgramTransfer
{
    private readonly ILogger<GskWebServerDriver> _logger;
    private readonly HttpClient _http;
    private readonly HttpClient _management;
    private readonly HttpClient _uploadHttp;
    private readonly bool _ownsHttp;
    private ConnectionState _state = ConnectionState.Disconnected;
    private GskWebServerOptions? _options;
    private GskStaticMetadata? _staticMetadata;

    public ProtocolType Protocol => ProtocolType.GskWebServer;
    public ConnectionState State => _state;
    public DriverCapabilities Capabilities =>
        DriverCapabilities.Read | DriverCapabilities.Write |
        DriverCapabilities.Browse | DriverCapabilities.BatchRead |
        DriverCapabilities.FileTransfer;
    public bool SupportsResume => false;

    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(30);

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public GskWebServerDriver(ILogger<GskWebServerDriver> logger, HttpClient? http = null)
    {
        _logger = logger;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        _management = new HttpClient();
        _uploadHttp = new HttpClient { Timeout = UploadTimeout + TimeSpan.FromSeconds(10) };
        _uploadHttp.DefaultRequestHeaders.ExpectContinue = false;
    }

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        Transition(ConnectionState.Connecting);
        _options = GskWebServerOptions.From(config);
        _http.BaseAddress = _options.BaseUri;
        _http.Timeout = config.ReadTimeout > TimeSpan.Zero ? config.ReadTimeout : TimeSpan.FromSeconds(5);
        _management.BaseAddress = _options.ManagementBaseUri;
        _management.Timeout = _http.Timeout;
        _uploadHttp.BaseAddress = _options.BaseUri;
        ApplyBasicAuth(config);
        ApplyWorkshopToken(_options.WorkshopAuthToken);

        try
        {
            using var response = await _http.GetAsync(Path(_options.HealthPath), ct);
            if (!response.IsSuccessStatusCode)
                return Fault($"GSK WebServer health check failed: {(int)response.StatusCode} {response.ReasonPhrase}");

            await TryLoadStaticMetadataAsync(ct);
            Transition(ConnectionState.Connected);
            StartRealtimeFeed();
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GSK WebServer connect failed");
            return Fault(ex.Message);
        }
    }

    private bool TryBuildToolLifeWrite(string address, out HttpMethod method,
        out string requestPath, out object body, out string? error)
    {
        var options = _options!;
        method = HttpMethod.Put;
        requestPath = "";
        body = new object();
        error = null;

        var sub = TailPart(address, 0).ToLowerInvariant();
        var group = IntTail(address, 1);
        var path = IntTail(address, 2);
        switch (sub)
        {
            case "attr":
                requestPath = Combine(options.ToolLifeReadPath, "attr");
                body = new { group, path, type = IntTail(address, 3), preset = IntTail(address, 4) };
                return true;
            case "prop":
            case "repo":
                requestPath = Combine(options.ToolLifeReadPath, sub);
                body = ToolLifePropBody(address, group, path);
                return true;
            case "delete":
                method = HttpMethod.Delete;
                requestPath = AppendQuery(AppendQuery(options.ToolLifeReadPath, "group", group.ToString()), "path", path.ToString());
                return true;
            default:
                error = "ToolLife write requires Attr, Prop, Repo, or Delete.";
                return false;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await StopRealtimeFeedAsync();
        Transition(ConnectionState.Disconnected);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_options is null) return false;
        try
        {
            using var response = await _http.GetAsync(Path(_options.HealthPath), ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        EnsureConnected();
        var normalized = NormalizeAddress(address);

        if (Prefix(normalized) == "realtime")
        {
            await WaitForFirstRealtimeFrameAsync(TimeSpan.FromSeconds(5), ct);
            if (TryGetRealtimeValue(normalized, out var realtime, out var rtError))
                return ToTagValue(normalized, dataType, realtime);
            return BadTag(normalized, dataType,
                $"{rtError} Realtime fields are only published via WebSocket; " +
                "the HTTP '/mc' endpoint serves static metadata and cannot provide '" + normalized + "'.");
        }

        if (!TryResolveReadEndpoint(normalized, out var client, out var requestPath, out var error))
            return BadTag(normalized, dataType, error);

        var document = await GetJsonAsync(client, requestPath, ct);
        var value = ExtractAddressValue(document.RootElement, normalized);
        return ToTagValue(normalized, dataType, value);
    }

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        var values = new List<TagValue>(requests.Count);
        foreach (var request in requests)
            values.Add(await ReadTagAsync(request.Address, request.DataType, ct));
        return values;
    }

    public async Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
    {
        EnsureConnected();
        var normalized = NormalizeAddress(address);
        if (!TryBuildWriteRequest(normalized, value, out var client, out var method, out var requestPath, out var body, out var error))
            return new() { Success = false, ErrorMessage = error };

        using var message = new HttpRequestMessage(method, Path(requestPath));
        if (method != HttpMethod.Delete)
            message.Content = JsonBody(body);
        using var response = await client.SendAsync(message, ct);
        return new()
        {
            Success = response.IsSuccessStatusCode,
            ErrorMessage = response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(ct)
        };
    }

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        IReadOnlyList<AddressNode> nodes = string.IsNullOrWhiteSpace(parentPath) || parentPath == "/"
            ? RootNodes()
            : ChildNodes(parentPath.Trim('/'));
        return Task.FromResult(nodes);
    }

    public Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Path,DisplayName,DataType,Readable,Writable");
        foreach (var node in RootNodes().Concat(KnownLeaves()))
            sb.AppendLine($"{node.Path},{node.DisplayName},{node.DataType},{node.IsReadable},{node.IsWritable}");
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public async Task<IReadOnlyList<ProgramFileEntry>> BrowseFilesAsync(string? path = null, CancellationToken ct = default)
    {
        EnsureConnected();
        using var document = await GetJsonAsync(_options!.ProgramListPath, ct);
        return ParseProgramFiles(document.RootElement);
    }

    public async Task<TransferProgressResult> UploadProgramAsync(
        Stream source, NCProgramMetadata metadata,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var transferId = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        var totalBytes = metadata.FileSize ?? (source.CanSeek ? source.Length : 0);
        var fileName = NormalizeProgramName(metadata.FileName);
        var uploadPath = Path(_options!.ProgramUploadPath);
        _logger.LogInformation("GSK upload start: file={File} bytes={Bytes} path={Path}", fileName, totalBytes, uploadPath);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(UploadTimeout);
        try
        {
            using var content = new MultipartFormDataContent();
            var boundaryParam = content.Headers.ContentType!.Parameters
                .First(p => p.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase));
            boundaryParam.Value = boundaryParam.Value!.Trim('"');
            var fileContent = new StreamContent(source);
            fileContent.Headers.ContentDisposition = new("form-data") { Name = "\"gskworkshop\"", FileName = $"\"{fileName}\"" };
            content.Add(fileContent);
            using var request = new HttpRequestMessage(HttpMethod.Post, uploadPath) { Content = content };
            request.Headers.ExpectContinue = false;
            using var response = await _uploadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            sw.Stop();
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
                _logger.LogInformation("GSK upload ok: file={File} bytes={Bytes} ms={Ms}", fileName, totalBytes, sw.ElapsedMilliseconds);
            else
                _logger.LogError("GSK upload fail: file={File} status={Status} body={Body}", fileName, (int)response.StatusCode, body);
            progress?.Report(new() { BytesTransferred = totalBytes, TotalBytes = totalBytes });
            return new()
            {
                Success = response.IsSuccessStatusCode,
                TransferId = transferId,
                BytesTransferred = response.IsSuccessStatusCode ? totalBytes : 0,
                Duration = sw.Elapsed,
                ErrorMessage = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}: {body}"
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            var msg = $"GSK upload timeout after {UploadTimeout.TotalSeconds:0}s (file={fileName}, bytes={totalBytes})";
            _logger.LogError(msg);
            return FailedTransfer(transferId, sw.Elapsed, msg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "GSK upload exception: file={File}", fileName);
            return FailedTransfer(transferId, sw.Elapsed, ex.Message);
        }
    }

    public async Task<TransferProgressResult> DownloadProgramAsync(
        string remotePath, Stream destination,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var transferId = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        var requestPath = Combine(_options!.ProgramDownloadPath, NormalizeProgramName(remotePath));

        try
        {
            using var response = await _http.GetAsync(Path(requestPath), ct);
            if (!response.IsSuccessStatusCode)
                return FailedTransfer(transferId, sw.Elapsed, await response.Content.ReadAsStringAsync(ct));

            await response.Content.CopyToAsync(destination, ct);
            sw.Stop();
            var bytes = destination.CanSeek ? destination.Position : 0;
            progress?.Report(new() { BytesTransferred = bytes, TotalBytes = bytes });
            return new() { Success = true, TransferId = transferId, BytesTransferred = bytes, Duration = sw.Elapsed };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return FailedTransfer(transferId, sw.Elapsed, ex.Message);
        }
    }

    public Task<TransferProgressResult> ResumeUploadAsync(string transferId, string remotePath, Stream source, long offset,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(FailedTransfer(transferId, TimeSpan.Zero, "GSK WebServer driver does not support partial resume upload."));

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _management.Dispose();
        _uploadHttp.Dispose();
        if (_ownsHttp) _http.Dispose();
    }

    private bool TryResolveReadEndpoint(string address, out HttpClient client, out string path, out string? error)
    {
        var options = _options!;
        var prefix = Prefix(address);
        if (prefix == "workshop")
        {
            client = _management;
            path = options.WorkshopPath;
            error = null;
            return true;
        }

        client = _http;
        path = prefix switch
        {
            "static" => options.StaticDataPath,
            "macro" => Combine(options.MacroReadPath, TailPart(address, 0)),
            "param" => ParamPath(options.ParamReadPath, address),
            "diagnose" => ParamPath(options.DiagnoseReadPath, address),
            "softplc" => SoftPlcPath(options.SoftPlcReadPath, address),
            "tool" => ToolOffsetPath(options.ToolOffsetReadPath, address),
            "toollife" => ToolLifePath(options.ToolLifeReadPath, address),
            "alarm" => InfoIndexPath(options.AlarmReadPath, address),
            "history" => InfoIndexPath(options.HistoryReadPath, address),
            _ => ""
        };

        error = prefix == "realtime"
            ? $"GSK realtime fields ('{address}') are not exposed over HTTP; the WebSocket stream is the only source."
            : string.IsNullOrWhiteSpace(path)
                ? $"Unknown GSK WebServer address '{address}'."
                : null;
        return error is null;
    }

    private static string ToolOffsetPath(string basePath, string address)
    {
        var seg = TailPart(address, 0);
        return string.IsNullOrWhiteSpace(seg) ? basePath
            : seg.Equals("totalcount", StringComparison.OrdinalIgnoreCase)
                ? Combine(basePath, "total-count")
                : Combine(basePath, seg);
    }

    private static string ToolLifePath(string basePath, string address)
    {
        var sub = TailPart(address, 0).ToLowerInvariant();
        var arg = TailPart(address, 1);
        var arg2 = TailPart(address, 2);
        return sub switch
        {
            "" => basePath,
            "totalcount" => Combine(basePath, "total-count"),
            "current" => AppendQuery(Combine(basePath, "current"), "group", arg),
            "attr" => AppendQuery(Combine(basePath, "attr"), "group", arg),
            "prop" => AppendQuery(AppendQuery(Combine(basePath, "prop"), "group", arg), "tool", arg2),
            _ => Combine(basePath, sub)
        };
    }

    private static string InfoIndexPath(string basePath, string address)
    {
        var seg = TailPart(address, 0);
        return string.IsNullOrWhiteSpace(seg) ? basePath
            : seg.Equals("totalcount", StringComparison.OrdinalIgnoreCase)
                ? Combine(basePath, "total-count")
                : Combine(basePath, seg);
    }

    private bool TryResolveReadPath(string address, out string path, out string? error)
    {
        var ok = TryResolveReadEndpoint(address, out _, out path, out error);
        return ok;
    }

    private bool TryBuildWriteRequest(string address, object value,
        out HttpClient client, out HttpMethod method, out string requestPath, out object body, out string? error)
    {
        var options = _options!;
        client = _http;
        method = HttpMethod.Put;
        requestPath = "";
        body = new object();
        error = null;

        switch (Prefix(address))
        {
            case "macro":
            {
                var id = TailPart(address, 0);
                var path = TailPart(address, 1);
                if (!int.TryParse(id, out var macroId))
                {
                    error = $"Macro write requires numeric id; got '{id}'.";
                    return false;
                }
                requestPath = Combine(options.MacroWritePath, id);
                body = new
                {
                    id = macroId,
                    path = int.TryParse(path, out var p) ? p : 0,
                    type = 0,
                    value
                };
                return true;
            }
            case "param":
            {
                var id = TailPart(address, 0);
                var idx = TailPart(address, 1);
                if (string.IsNullOrWhiteSpace(id))
                {
                    error = "Param write requires id.";
                    return false;
                }
                requestPath = Combine(options.ParamWritePath, id);
                body = new
                {
                    index = int.TryParse(idx, out var i) ? i : 0,
                    value
                };
                return true;
            }
            case "softplc":
            {
                var register = TailPart(address, 0);
                var id = TailPart(address, 1);
                if (string.IsNullOrWhiteSpace(register) || string.IsNullOrWhiteSpace(id))
                {
                    error = "SoftPlc write requires register kind and id (e.g. SoftPlc:R:100).";
                    return false;
                }
                requestPath = Combine(Combine(options.SoftPlcWritePath, register.ToLowerInvariant()), id);
                body = new { value };
                return true;
            }
            case "program":
            {
                var sub = TailPart(address, 0).ToLowerInvariant();
                if (sub != "load")
                {
                    error = $"Unsupported program write '{sub}'. Use 'Program.Load' with program name as value.";
                    return false;
                }
                var pathSegment = TailPart(address, 1);
                requestPath = options.ProgramLoadPath;
                body = new
                {
                    program = value?.ToString() ?? "",
                    path = int.TryParse(pathSegment, out var p) ? p : 0,
                    loaded = true
                };
                return true;
            }
            case "tool":
            {
                var id = TailPart(address, 0);
                if (!int.TryParse(id, out _))
                {
                    error = $"Tool offset write requires numeric id; got '{id}'. Use 'Tool:{{id}}:{{path}}:{{type}}:{{axis}}'.";
                    return false;
                }
                requestPath = Combine(options.ToolOffsetWritePath, id);
                body = new
                {
                    path = int.TryParse(TailPart(address, 1), out var p) ? p : 0,
                    type = int.TryParse(TailPart(address, 2), out var t) ? t : 0,
                    axis = int.TryParse(TailPart(address, 3), out var a) ? a : 0,
                    value
                };
                return true;
            }
            case "toollife":
                return TryBuildToolLifeWrite(address, out method, out requestPath, out body, out error);
            case "workshop":
                client = _management;
                method = HttpMethod.Post;
                requestPath = options.WorkshopPath;
                body = value;
                return true;
            default:
                error = $"Write not supported for GSK WebServer address '{address}'.";
                return false;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(HttpClient client, string requestPath, CancellationToken ct)
    {
        using var response = await client.GetAsync(Path(requestPath), ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private Task<JsonDocument> GetJsonAsync(string requestPath, CancellationToken ct) =>
        GetJsonAsync(_http, requestPath, ct);

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected || _options is null)
            throw new InvalidOperationException("GSK WebServer driver is not connected.");
    }

    private ConnectionResult Fault(string message)
    {
        Transition(ConnectionState.Faulted, message);
        return new() { Success = false, ErrorMessage = message };
    }

    private static TransferProgressResult FailedTransfer(string transferId, TimeSpan duration, string message) => new()
    {
        Success = false,
        TransferId = transferId,
        BytesTransferred = 0,
        Duration = duration,
        ErrorMessage = message
    };

    private static TagValue BadTag(string address, DataType dataType, string? message) => new()
    {
        Address = address,
        DataType = dataType,
        Value = "",
        Quality = TagQuality.Bad,
        Timestamp = DateTimeOffset.UtcNow,
        ErrorMessage = message
    };

    private void Transition(ConnectionState next, string? reason = null)
    {
        var old = _state;
        if (old == next) return;
        _state = next;
        StateChanged?.Invoke(this, new() { OldState = old, NewState = next, Reason = reason });
    }

    private void ApplyBasicAuth(DeviceConnectionConfig config)
    {
        _http.DefaultRequestHeaders.Authorization = null;
        _uploadHttp.DefaultRequestHeaders.Authorization = null;
        if (string.IsNullOrWhiteSpace(config.Username)) return;

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{config.Password ?? ""}"));
        var header = new AuthenticationHeaderValue("Basic", token);
        _http.DefaultRequestHeaders.Authorization = header;
        _uploadHttp.DefaultRequestHeaders.Authorization = header;
    }

    private void ApplyWorkshopToken(string? token)
    {
        _management.DefaultRequestHeaders.Remove("X-Authorization-Token");
        if (!string.IsNullOrWhiteSpace(token))
            _management.DefaultRequestHeaders.Add("X-Authorization-Token", token);
    }

    private async Task TryLoadStaticMetadataAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync(_options!.StaticDataPath, ct);
            _staticMetadata = ParseStaticMetadata(doc.RootElement);
            _logger.LogInformation(
                "GSK static metadata loaded: pathCount={Path} axisCount={Axes} spindleCount={Spindles}",
                _staticMetadata?.PathCount, _staticMetadata?.AxisCount, _staticMetadata?.SpindleCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GSK static metadata load failed; address browse falls back to single-axis defaults");
            _staticMetadata = null;
        }
    }

    private static GskStaticMetadata ParseStaticMetadata(JsonElement root)
    {
        var payload = Unwrap(root);
        if (payload.ValueKind == JsonValueKind.Array && payload.GetArrayLength() > 0)
            payload = payload[0];

        var pathCount = TryGetProperty(payload, "pathcount", out var pc) && pc.TryGetInt32(out var p) ? p : 1;

        var axisCount = 0;
        IReadOnlyList<string> namesAbsolute = [];
        IReadOnlyList<string> namesRelative = [];
        IReadOnlyList<string> namesMachine = [];
        IReadOnlyList<string> namesRemain = [];
        if (TryGetProperty(payload, "axes", out var axes) &&
            axes.ValueKind == JsonValueKind.Array && axes.GetArrayLength() > 0)
        {
            var first = axes[0];
            if (TryGetProperty(first, "totalcount", out var tc) && tc.TryGetInt32(out var ax))
                axisCount = ax;
            if (TryGetProperty(first, "names", out var names))
            {
                namesAbsolute = ReadStringArray(names, "absolute");
                namesRelative = ReadStringArray(names, "relative");
                namesMachine = ReadStringArray(names, "machine");
                namesRemain = ReadStringArray(names, "remain");
            }
        }

        var spindleCount = 0;
        if (TryGetProperty(payload, "spindle", out var sp) &&
            sp.ValueKind == JsonValueKind.Array && sp.GetArrayLength() > 0 &&
            TryGetProperty(sp[0], "totalcount", out var stc) && stc.TryGetInt32(out var sc))
            spindleCount = sc;

        return new GskStaticMetadata(
            Math.Max(1, pathCount), Math.Max(0, axisCount), Math.Max(0, spindleCount),
            namesAbsolute, namesRelative, namesMachine, namesRemain);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string name)
    {
        if (!TryGetProperty(parent, name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
            list.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString());
        return list;
    }

    private static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static string Path(string path) => path.TrimStart('/');

    private static string NormalizeAddress(string address) => address.Trim().TrimStart('/');

    private static string AppendQuery(string path, string name, string value)
    {
        var separator = path.Contains('?') ? "&" : "?";
        return $"{path}{separator}{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
    }

    private static string Combine(string path, string segment) =>
        $"{path.TrimEnd('/')}/{Uri.EscapeDataString(segment.Trim('/'))}";

    private static string ParamPath(string basePath, string address)
    {
        var id = TailPart(address, 0);
        var axis = TailPart(address, 1);
        return string.IsNullOrWhiteSpace(axis) ? Combine(basePath, id) : AppendQuery(Combine(basePath, id), "axis", axis);
    }

    private static string SoftPlcPath(string basePath, string address)
    {
        var register = TailPart(address, 0).ToLowerInvariant();
        var id = TailPart(address, 1);
        return Combine(Combine(basePath, register), id);
    }

    private static string Prefix(string address)
    {
        var index = address.IndexOfAny(['.', ':']);
        return (index > 0 ? address[..index] : address).ToLowerInvariant();
    }

    private static string Tail(string address)
    {
        var index = address.IndexOfAny(['.', ':']);
        return index >= 0 && index + 1 < address.Length ? address[(index + 1)..] : address;
    }

    private static string TailPart(string address, int partIndex)
    {
        var parts = Tail(address).Split([':', '.'], StringSplitOptions.RemoveEmptyEntries);
        return partIndex < parts.Length ? parts[partIndex] : "";
    }

    private static int IntTail(string address, int partIndex) =>
        int.TryParse(TailPart(address, partIndex), out var value) ? value : 0;

    private static object ToolLifePropBody(string address, int group, int path) => new
    {
        group,
        path,
        tool = IntTail(address, 3),
        which = TailPart(address, 0).Equals("repo", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
        jumped = bool.TryParse(TailPart(address, 4), out var jumped) && jumped,
        tno = IntTail(address, 5),
        tofs = IntTail(address, 6)
    };

    private static string NormalizeProgramName(string remotePath)
    {
        var name = remotePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? remotePath;
        return name.EndsWith(".CNC", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.CNC";
    }
}
