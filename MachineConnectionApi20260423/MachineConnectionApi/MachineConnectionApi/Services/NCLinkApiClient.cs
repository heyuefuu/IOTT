using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MachineConnectionApi.Options;
using Microsoft.Extensions.Options;

namespace MachineConnectionApi.Services;

// ── DTO ─────────────────────────────────────────────────────────────────

public sealed class NCLinkApiRequest
{
    [JsonPropertyName("operation")]
    public required string Operation { get; init; }

    [JsonPropertyName("items")]
    public required IReadOnlyList<NCLinkApiRequestItem> Items { get; init; }
}

public sealed record NCLinkApiRequestItem
{
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("index")] public JsonNode? Index { get; init; }
    [JsonPropertyName("value")] public JsonNode? Value { get; init; }
    [JsonPropertyName("offset")] public int? Offset { get; init; }
    [JsonPropertyName("key")] public JsonNode? Key { get; init; }
    [JsonPropertyName("length")] public int? Length { get; init; }
    [JsonPropertyName("timeout")] public int? Timeout { get; init; }
}

public sealed class NCLinkApiResponse
{
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("code")] public int Code { get; init; }
    [JsonPropertyName("value")] public JsonNode? Value { get; init; }

    public bool IsSuccess =>
        Code == 0 && Status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase);
}

// ── Client ──────────────────────────────────────────────────────────────

public interface INCLinkApiClient
{
    /// <summary>批量读取华中机床点位值。返回的 response.Value 是 items×index 的二维数组。</summary>
    Task<NCLinkApiResponse> BatchGetValueAsync(
        string deviceId,
        IReadOnlyList<NCLinkApiRequestItem> items,
        CancellationToken ct = default);

    Task<NCLinkApiResponse> BatchDataAsync(
        string deviceId,
        string operation,
        IReadOnlyList<NCLinkApiRequestItem> items,
        CancellationToken ct = default);
}

public sealed class NCLinkApiClient : INCLinkApiClient
{
    public const string HttpClientName = "NCLinkApi";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<NCLinkApiOptions> _options;
    private readonly ILogger<NCLinkApiClient> _logger;

    public NCLinkApiClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<NCLinkApiOptions> options,
        ILogger<NCLinkApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<NCLinkApiResponse> BatchGetValueAsync(
        string deviceId,
        IReadOnlyList<NCLinkApiRequestItem> items,
        CancellationToken ct = default)
        => await BatchDataAsync(deviceId, "get_value", items, ct);

    public async Task<NCLinkApiResponse> BatchDataAsync(
        string deviceId,
        string operation,
        IReadOnlyList<NCLinkApiRequestItem> items,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId required", nameof(deviceId));
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("operation required", nameof(operation));
        if (items == null || items.Count == 0)
            throw new ArgumentException("items required", nameof(items));

        var opt = _options.CurrentValue;
        if (!opt.Enabled)
            throw new InvalidOperationException("NCLinkApi 未启用，请在 appsettings.json 中将 NCLinkApi:Enabled 置为 true。");

        var defaultTimeout = Math.Max(100, opt.DefaultItemTimeoutMs);
        var normalizedItems = items
            .Select(i => i with { Timeout = i.Timeout ?? defaultTimeout })
            .ToList();

        var payload = new NCLinkApiRequest
        {
            Operation = operation.Trim(),
            Items = normalizedItems,
        };

        var url = $"/v1/{Uri.EscapeDataString(deviceId.Trim())}/data/";
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        _logger.LogDebug("NCLinkApi → POST {Url} {Body}", url, json);

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        _logger.LogDebug("NCLinkApi ← {Status} {Body}", (int)response.StatusCode, body);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"NC-Link HTTP {(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<NCLinkApiResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("NC-Link 响应体为空");
    }
}
