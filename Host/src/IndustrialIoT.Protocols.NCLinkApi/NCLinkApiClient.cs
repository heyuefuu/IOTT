namespace IndustrialIoT.Protocols.NCLinkApi;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Extensions.Logging;

/// <summary>
/// NC-Link API Server 的 HTTP 通信封装 — 对照《NC-Link应用开发指导手册》第 5 章。
/// 基地址形如 http://host:19001/，所有 path 形如 /v1/{device_id}/...。
/// 该类无状态、线程安全，由 NCLinkApiDriver 持有。
/// </summary>
public sealed class NCLinkApiClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,  // 手册字段全部小写下划线，保持原样
    };

    private readonly HttpClient _http;
    private readonly ILogger? _logger;
    private readonly bool _ownsHttpClient;

    /// <summary>构造：使用外部传入 HttpClient（推荐，DI 场景）。</summary>
    public NCLinkApiClient(HttpClient http, ILogger? logger = null)
    {
        _http = http;
        _logger = logger;
        _ownsHttpClient = false;
        if (_http.BaseAddress is null)
            throw new ArgumentException("HttpClient.BaseAddress is required", nameof(http));
    }

    /// <summary>构造：内部创建 HttpClient。</summary>
    public NCLinkApiClient(Uri baseAddress, TimeSpan? timeout = null, ILogger? logger = null)
    {
        _http = new HttpClient { BaseAddress = baseAddress, Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        _logger = logger;
        _ownsHttpClient = true;
    }

    // ── 数据接口 /v1/{device_id}/data/ ────────────────────────────────────

    /// <summary>
    /// POST /v1/{device_id}/data/  — 手册 5.1（注意 GET/POST 共用同一路径，请求体决定操作）。
    /// 用 POST 是 Spring 默认对带 body 的接口的实际行为；手册示例 GET 在 nclink-api-server 0.3.10 中实测亦走 POST。
    /// </summary>
    public async Task<NCLinkApiResponse> InvokeAsync(
        string deviceId, NCLinkApiRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId required", nameof(deviceId));

        var url = $"/v1/{Uri.EscapeDataString(deviceId)}/data/";
        var json = JsonSerializer.Serialize(request, JsonOptions);
        _logger?.LogDebug("NCLinkApi → POST {Url} {Body}", url, json);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        _logger?.LogDebug("NCLinkApi ← {Status} {Body}", (int)resp.StatusCode, body);

        if (!resp.IsSuccessStatusCode)
            throw new NCLinkApiException(
                $"NC-Link HTTP {(int)resp.StatusCode}: {body}");

        var parsed = JsonSerializer.Deserialize<NCLinkApiResponse>(body, JsonOptions)
            ?? throw new NCLinkApiException("Empty response body");
        return parsed;
    }

    /// <summary>调用并在失败时抛异常。</summary>
    public async Task<NCLinkApiResponse> InvokeOrThrowAsync(
        string deviceId, NCLinkApiRequest request, CancellationToken ct = default)
    {
        var resp = await InvokeAsync(deviceId, request, ct).ConfigureAwait(false);
        if (!resp.IsSuccess)
            throw new NCLinkApiException(resp.StatusCode, resp.Status,
                $"path={request.Items.FirstOrDefault()?.Path} op={request.Operation}");
        return resp;
    }

    // ── 文件接口 /v1/{device_id}/file/ ────────────────────────────────────

    /// <summary>
    /// GET /v1/{device_id}/file/?key=Otemp  — 手册 5.2.1，response body 即文件原始内容。
    /// 可选 offset/length 做分段下载。
    /// </summary>
    public async Task<byte[]> DownloadFileAsync(
        string deviceId, string key, long? offset = null, int? length = null,
        CancellationToken ct = default)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["key"] = key;
        if (offset.HasValue) query["offset"] = offset.Value.ToString();
        if (length.HasValue) query["length"] = length.Value.ToString();
        var url = $"/v1/{Uri.EscapeDataString(deviceId)}/file/?{query}";

        _logger?.LogDebug("NCLinkApi file GET {Url}", url);
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new NCLinkApiException($"NC-Link file download HTTP {(int)resp.StatusCode}: {body}");
        }
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// POST /v1/{device_id}/file/  — 手册 5.2.2，multipart/form-data。
    /// 表单字段：key（远程路径+文件名）、operation=set_value、path、value（文件流）。
    /// </summary>
    public async Task<NCLinkApiResponse> UploadFileAsync(
        string deviceId, string remoteKey, Stream content, string? fileName = null,
        string path = NCLinkApiPaths.File, CancellationToken ct = default)
    {
        var url = $"/v1/{Uri.EscapeDataString(deviceId)}/file/";

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(remoteKey), "key");
        form.Add(new StringContent(NCLinkApiOperations.SetValue), "operation");
        form.Add(new StringContent(path), "path");

        var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "value", fileName ?? remoteKey.Split('/').LastOrDefault() ?? "file");

        _logger?.LogDebug("NCLinkApi file POST {Url} key={Key}", url, remoteKey);
        using var resp = await _http.PostAsync(url, form, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new NCLinkApiException($"NC-Link file upload HTTP {(int)resp.StatusCode}: {body}");

        var parsed = JsonSerializer.Deserialize<NCLinkApiResponse>(body, JsonOptions)
            ?? throw new NCLinkApiException("Empty response body");
        if (!parsed.IsSuccess)
            throw new NCLinkApiException(parsed.StatusCode, parsed.Status, $"upload {remoteKey}");
        return parsed;
    }

    // ── 便捷封装 ──────────────────────────────────────────────────────────

    /// <summary>读取单个数据项（标量 index 或 int[] index 都支持）。</summary>
    public Task<NCLinkApiResponse> GetValueAsync(
        string deviceId, string path, JsonNode? index = null,
        int? timeoutMs = null, CancellationToken ct = default)
    {
        return InvokeAsync(deviceId, new NCLinkApiRequest
        {
            Operation = NCLinkApiOperations.GetValue,
            Items = [new NCLinkApiRequestItem { Path = path, Index = index, Timeout = timeoutMs }],
        }, ct);
    }

    /// <summary>设置数据项（含 index/offset 位写入）。</summary>
    public Task<NCLinkApiResponse> SetValueAsync(
        string deviceId, string path, JsonNode value,
        JsonNode? index = null, int? offset = null, JsonNode? key = null,
        int? timeoutMs = null, CancellationToken ct = default)
    {
        return InvokeAsync(deviceId, new NCLinkApiRequest
        {
            Operation = NCLinkApiOperations.SetValue,
            Items = [new NCLinkApiRequestItem
            {
                Path = path, Value = value, Index = index, Offset = offset, Key = key,
                Timeout = timeoutMs,
            }],
        }, ct);
    }

    /// <summary>批量请求 — 手册 8.2 支持一次请求多个不同数据项。</summary>
    public Task<NCLinkApiResponse> BatchGetValueAsync(
        string deviceId, IReadOnlyList<NCLinkApiRequestItem> items,
        CancellationToken ct = default)
    {
        return InvokeAsync(deviceId, new NCLinkApiRequest
        {
            Operation = NCLinkApiOperations.GetValue,
            Items = items,
        }, ct);
    }

    public Task<NCLinkApiResponse> GetKeysAsync(
        string deviceId, string path, CancellationToken ct = default)
    {
        return InvokeAsync(deviceId, new NCLinkApiRequest
        {
            Operation = NCLinkApiOperations.GetKeys,
            Items = [new NCLinkApiRequestItem { Path = path }],
        }, ct);
    }

    public Task<NCLinkApiResponse> GetAttributesAsync(
        string deviceId, string path, JsonNode key, CancellationToken ct = default)
    {
        return InvokeAsync(deviceId, new NCLinkApiRequest
        {
            Operation = NCLinkApiOperations.GetAttributes,
            Items = [new NCLinkApiRequestItem { Path = path, Key = key }],
        }, ct);
    }

    public Task<NCLinkApiResponse> AddAsync(
        string deviceId, string path, JsonNode? key = null, JsonNode? value = null,
        CancellationToken ct = default)
    {
        return InvokeAsync(deviceId, new NCLinkApiRequest
        {
            Operation = NCLinkApiOperations.Add,
            Items = [new NCLinkApiRequestItem { Path = path, Key = key, Value = value }],
        }, ct);
    }

    public Task<NCLinkApiResponse> DeleteAsync(
        string deviceId, string path, JsonNode? key = null,
        CancellationToken ct = default)
    {
        return InvokeAsync(deviceId, new NCLinkApiRequest
        {
            Operation = NCLinkApiOperations.Delete,
            Items = [new NCLinkApiRequestItem { Path = path, Key = key }],
        }, ct);
    }

    /// <summary>设备活跃检测 — API Server 提供的小工具接口（手册 7.1）。</summary>
    public async Task<bool> CheckDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(
                $"/api/tools/check-device?deviceId={Uri.EscapeDataString(deviceId)}", ct)
                .ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient) _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
