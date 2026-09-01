namespace IndustrialIoT.Infrastructure.Messaging;

using System.Net.Http.Json;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class HttpWebhookOutputOptions
{
    public const string SectionName = "DataOutput:HttpWebhook";
    public bool Enabled { get; set; }
    public string EndpointUrl { get; set; } = "";
    public string? BearerToken { get; set; }
}

public sealed class HttpWebhookDataOutput : IDataOutput, IDisposable
{
    private readonly HttpWebhookOutputOptions _options;
    private readonly ILogger<HttpWebhookDataOutput> _logger;
    private readonly HttpClient _http = new();

    public HttpWebhookDataOutput(IOptions<HttpWebhookOutputOptions> options, ILogger<HttpWebhookDataOutput> logger)
    {
        _options = options.Value;
        _logger = logger;
        if (!string.IsNullOrWhiteSpace(_options.BearerToken))
            _http.DefaultRequestHeaders.Authorization =
                new("Bearer", _options.BearerToken);
    }

    public string Name => "HttpWebhook";
    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task WriteAsync(CollectedDataBatch batch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.EndpointUrl))
            throw new InvalidOperationException("DataOutput:HttpWebhook:EndpointUrl is required.");

        using var response = await _http.PostAsJsonAsync(_options.EndpointUrl, batch, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Webhook returned {(int)response.StatusCode}: {body}");
        }
        _logger.LogDebug("Posted realtime batch to webhook {Endpoint}", _options.EndpointUrl);
    }

    public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public void Dispose() => _http.Dispose();
}
