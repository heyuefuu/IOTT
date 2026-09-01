using System.Text;
using System.Text.Json;
using MachineConnectionApi.Options;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MachineConnectionApi.Services;

public interface INCLinkModelProbeClient
{
    Task<string?> LoadModelJsonAsync(string deviceId, CancellationToken ct = default);
}

public sealed class NCLinkModelProbeClient : INCLinkModelProbeClient
{
    private readonly IOptionsMonitor<MqttOptions> _options;
    private readonly ILogger<NCLinkModelProbeClient> _logger;

    public NCLinkModelProbeClient(
        IOptionsMonitor<MqttOptions> options,
        ILogger<NCLinkModelProbeClient> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<string?> LoadModelJsonAsync(string deviceId, CancellationToken ct = default)
    {
        var opt = _options.CurrentValue;
        if (!opt.Enabled || string.IsNullOrWhiteSpace(deviceId))
            return null;

        var responseTopic = $"Probe/Query/Response/{deviceId.Trim()}";
        var requestTopic = $"Probe/Query/Request/{deviceId.Trim()}";
        using var client = new MqttFactory().CreateMqttClient();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, opt.ConnectTimeoutSeconds)));

        var tcs = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += e =>
        {
            if (!string.Equals(e.ApplicationMessage.Topic, responseTopic, StringComparison.Ordinal))
                return Task.CompletedTask;

            var payload = DecodePayload(e.ApplicationMessage.PayloadSegment);
            tcs.TrySetResult(WrapProbePayload(payload));
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

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(requestTopic)
                .WithPayload(Array.Empty<byte>())
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await client.PublishAsync(message, linked.Token).ConfigureAwait(false);

            await using (linked.Token.Register(() => tcs.TrySetResult(null)))
            {
                var modelJson = await tcs.Task.ConfigureAwait(false);
                if (modelJson is null)
                {
                    _logger.LogWarning(
                        "NC-Link MQTT Probe 未收到响应: requestTopic={RequestTopic}, responseTopic={ResponseTopic}",
                        requestTopic,
                        responseTopic);
                }
                return modelJson;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NC-Link MQTT Probe 加载模型失败: deviceId={DeviceId}", deviceId);
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
        var clientId = $"MachineConnectionApi-Probe-{Guid.NewGuid():N}";
        var b = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithClientId(clientId)
            .WithTcpServer(opt.Host.Trim(), opt.Port);
        if (opt.UseTls)
            b = b.WithTlsOptions(o => o.UseTls());
        if (!string.IsNullOrEmpty(opt.Username))
            b = b.WithCredentials(opt.Username, opt.Password ?? "");
        return b.Build();
    }

    private static string DecodePayload(ArraySegment<byte> payload) =>
        payload.Array is null ? "" : Encoding.UTF8.GetString(payload.Array, payload.Offset, payload.Count);

    private static string? WrapProbePayload(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.TryGetProperty("probe", out var probe)
            ? $$"""{"status":"SUCCESS","code":0,"value":{{probe.GetRawText()}}}"""
            : null;
    }
}
