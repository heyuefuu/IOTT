using System.Text;
using System.Text.Json;
using MachineConnectionApi.Options;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MachineConnectionApi.Services;

public interface INCLinkMqttQueryClient
{
    Task<NCLinkMqttQueryResponse?> QueryAsync(
        string deviceId,
        IReadOnlyList<string> sourceIds,
        CancellationToken ct = default);
}

public sealed class NCLinkMqttQueryClient : INCLinkMqttQueryClient
{
    private readonly IOptionsMonitor<MqttOptions> _options;
    private readonly ILogger<NCLinkMqttQueryClient> _logger;

    public NCLinkMqttQueryClient(
        IOptionsMonitor<MqttOptions> options,
        ILogger<NCLinkMqttQueryClient> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<NCLinkMqttQueryResponse?> QueryAsync(
        string deviceId,
        IReadOnlyList<string> sourceIds,
        CancellationToken ct = default)
    {
        var opt = _options.CurrentValue;
        if (!opt.Enabled || string.IsNullOrWhiteSpace(deviceId) || sourceIds.Count == 0)
            return null;

        var requestId = $"MachineConnectionApi-{Guid.NewGuid():N}";
        var responseTopic = $"Query/Response/{deviceId.Trim()}";
        var requestTopic = $"Query/Request/{deviceId.Trim()}";
        using var client = new MqttFactory().CreateMqttClient();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, opt.ConnectTimeoutSeconds)));
        var tcs = new TaskCompletionSource<NCLinkMqttQueryResponse?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        client.ApplicationMessageReceivedAsync += e =>
        {
            if (!string.Equals(e.ApplicationMessage.Topic, responseTopic, StringComparison.Ordinal))
                return Task.CompletedTask;

            var payload = DecodePayload(e.ApplicationMessage.PayloadSegment);
            var response = TryParseResponse(payload, requestId);
            if (response is not null)
                tcs.TrySetResult(response);
            return Task.CompletedTask;
        };

        try
        {
            await client.ConnectAsync(BuildConnectOptions(opt), linked.Token).ConfigureAwait(false);
            var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(responseTopic)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                .Build();
            await client.SubscribeAsync(subscribeOptions, linked.Token).ConfigureAwait(false);

            var payload = BuildRequestPayload(requestId, sourceIds);
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(requestTopic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await client.PublishAsync(message, linked.Token).ConfigureAwait(false);

            await using (linked.Token.Register(() => tcs.TrySetResult(null)))
                return await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NC-Link MQTT Query 失败: deviceId={DeviceId}", deviceId);
            return null;
        }
        finally
        {
            if (client.IsConnected)
                await client.DisconnectAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static MqttClientOptions BuildConnectOptions(MqttOptions opt)
    {
        var b = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithClientId($"MachineConnectionApi-Query-{Guid.NewGuid():N}")
            .WithTcpServer(opt.Host.Trim(), opt.Port);
        if (opt.UseTls)
            b = b.WithTlsOptions(o => o.UseTls());
        if (!string.IsNullOrEmpty(opt.Username))
            b = b.WithCredentials(opt.Username, opt.Password ?? "");
        return b.Build();
    }

    private static string BuildRequestPayload(string requestId, IReadOnlyList<string> sourceIds)
    {
        var payload = new
        {
            @id = requestId,
            ids = sourceIds.Select(id => new { id }).ToArray(),
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string DecodePayload(ArraySegment<byte> payload) =>
        payload.Array is null ? "" : Encoding.UTF8.GetString(payload.Array, payload.Offset, payload.Count);

    private static NCLinkMqttQueryResponse? TryParseResponse(string payload, string requestId)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (!root.TryGetProperty("@id", out var id) || id.GetString() != requestId)
            return null;
        if (!root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            return null;
        return new NCLinkMqttQueryResponse(values.Clone());
    }
}

public sealed record NCLinkMqttQueryResponse(JsonElement Values);
