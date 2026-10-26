namespace MachineConnectionApi.Services;

using System.Collections.Concurrent;
using MachineConnectionApi.Models;
using MachineConnectionApi.Options;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

/// <summary>
/// Publishes collected batches to the MQTT targets configured on the "数据出口" page
/// (App_Data/data-output-targets.json). It subscribes to the Host collection topic
/// (industrial-iot/devices/{deviceId}/data) and the gateway telemetry topic
/// (machines/telemetry/{deviceId}), then re-publishes the raw payload to every enabled
/// MQTT target whose device filter matches (empty list = all devices).
///
/// Previously the data-output page was configuration + connectivity test only; nothing
/// consumed those entries. This service makes them effective for MQTT targets.
/// </summary>
public sealed class DataOutputForwarder : BackgroundService
{
    private static readonly JsonFileStore<DataOutputTargetDto> Targets = new("data-output-targets.json");
    private static readonly string[] SourceFilters = ["industrial-iot/devices/+/data", "machines/telemetry/+"];

    private readonly IOptionsMonitor<MqttOptions> _options;
    private readonly ILogger<DataOutputForwarder> _logger;
    private readonly ConcurrentDictionary<string, IMqttClient> _targetClients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _targetGate = new(1, 1);
    private IMqttClient? _source;
    private string? _sourceFingerprint;
    private string? _subscribedFingerprint;
    private int _disposed;

    public DataOutputForwarder(IOptionsMonitor<MqttOptions> options, ILogger<DataOutputForwarder> logger)
    {
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = _options.CurrentValue;
                if (!options.Enabled)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    continue;
                }
                await EnsureSourceAsync(options, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT 数据出口转发器未就绪，将重试");
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch { break; }
            }
        }
    }

    private async Task EnsureSourceAsync(MqttOptions options, CancellationToken ct)
    {
        var fingerprint = $"{options.Host}:{options.Port}:{options.Username}";
        if (_source is not null && _source.IsConnected && _sourceFingerprint == fingerprint)
        {
            return;
        }

        if (_source is not null)
        {
            try { await _source.DisconnectAsync(); } catch { }
            try { _source.Dispose(); } catch { }
            _source = null;
            _subscribedFingerprint = null;
        }

        var builder = new MqttClientOptionsBuilder()
            .WithClientId($"{options.ClientId}-outbox-{Guid.NewGuid():N}"[..Math.Min(23, options.ClientId.Length + 26)])
            .WithTcpServer(options.Host, options.Port)
            .WithCleanSession()
            .WithTimeout(TimeSpan.FromSeconds(Math.Max(3, options.ConnectTimeoutSeconds)));

        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            builder.WithCredentials(options.Username, options.Password);
        }
        if (options.UseTls)
        {
            builder.WithTlsOptions(o => o.UseTls());
        }

        var client = new MqttFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += OnMessageAsync;
        client.DisconnectedAsync += args =>
        {
            _logger.LogWarning("MQTT 数据出口转发器与 {Host}:{Port} 断开：{Reason}", options.Host, options.Port, args.Reason);
            return Task.CompletedTask;
        };

        var result = await client.ConnectAsync(builder.Build(), ct);
        if ((int)result.ResultCode >= 128)
        {
            throw new IOException($"MQTT source connection rejected: {result.ResultCode}");
        }

        _source = client;
        _sourceFingerprint = fingerprint;
        _subscribedFingerprint = null;

        var subscribe = new MqttClientSubscribeOptionsBuilder();
        foreach (var filter in SourceFilters)
        {
            subscribe.WithTopicFilter(filter, MqttQualityOfServiceLevel.AtLeastOnce);
        }
        var subResult = await client.SubscribeAsync(subscribe.Build(), ct);
        foreach (var item in subResult.Items)
        {
            if ((int)item.ResultCode >= 128)
            {
                _logger.LogWarning("MQTT 数据出口订阅 {Topic} 失败：{Code}", item.TopicFilter.Topic, item.ResultCode);
            }
        }
        _subscribedFingerprint = fingerprint;
        _logger.LogInformation("MQTT 数据出口转发器已连接 {Host}:{Port}，订阅 {Filters}",
            options.Host, options.Port, string.Join(", ", SourceFilters));
    }

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        try
        {
            var topic = args.ApplicationMessage.Topic;
            var deviceId = ExtractDeviceId(topic);
            if (string.IsNullOrWhiteSpace(deviceId)) return;

            var payload = args.ApplicationMessage.ConvertPayloadToString();
            if (string.IsNullOrEmpty(payload)) return;

            var targets = Targets.ReadAll()
                .Where(t => t.Enabled && string.Equals(t.Type, "MQTT", StringComparison.OrdinalIgnoreCase))
                .Where(t => t.DeviceIds.Count == 0 || t.DeviceIds.Contains(deviceId, StringComparer.Ordinal))
                .ToList();
            if (targets.Count == 0) return;

            foreach (var target in targets)
            {
                var targetTopic = string.IsNullOrWhiteSpace(target.Topic)
                    ? $"data-output/{deviceId}"
                    : target.Topic.Replace("{deviceId}", deviceId, StringComparison.Ordinal);
                // Never forward back into the source subscriptions: that would create a loop.
                if (targetTopic.StartsWith("industrial-iot/devices/", StringComparison.OrdinalIgnoreCase)
                    || targetTopic.StartsWith("machines/telemetry/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(target.Host) || target.Port is null or <= 0) continue;

                try
                {
                    var client = await GetTargetClientAsync(target, CancellationToken.None);
                    var message = new MqttApplicationMessageBuilder()
                        .WithTopic(targetTopic)
                        .WithPayload(payload)
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithRetainFlag(false)
                        .Build();
                    var result = await client.PublishAsync(message, CancellationToken.None);
                    if ((int)result.ReasonCode >= 128)
                    {
                        _logger.LogWarning("MQTT 数据出口发布被拒绝：target={Name} topic={Topic} code={Code}",
                            target.Name, targetTopic, result.ReasonCode);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MQTT 数据出口发布失败：target={Name} {Host}:{Port} topic={Topic}",
                        target.Name, target.Host, target.Port, targetTopic);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MQTT 数据出口转发消息处理失败");
        }
    }

    private async Task<IMqttClient> GetTargetClientAsync(DataOutputTargetDto target, CancellationToken ct)
    {
        var host = target.Host!.Trim();
        var port = target.Port!.Value;
        var key = $"{host}:{port}:{target.Username}";
        if (_targetClients.TryGetValue(key, out var existing) && existing.IsConnected) return existing;

        await _targetGate.WaitAsync(ct);
        try
        {
            if (_targetClients.TryGetValue(key, out existing) && existing.IsConnected) return existing;
            if (existing is not null) { try { existing.Dispose(); } catch { } _targetClients.TryRemove(key, out _); }

            var builder = new MqttClientOptionsBuilder()
                .WithClientId($"iott-fwd-{Guid.NewGuid():N}"[..23])
                .WithTcpServer(host, port)
                .WithCleanSession()
                .WithTimeout(TimeSpan.FromSeconds(5));
            if (!string.IsNullOrWhiteSpace(target.Username))
            {
                builder.WithCredentials(target.Username, target.Password);
            }

            var client = new MqttFactory().CreateMqttClient();
            var result = await client.ConnectAsync(builder.Build(), ct);
            if ((int)result.ResultCode >= 128)
            {
                throw new IOException($"MQTT target connection rejected: {result.ResultCode}");
            }
            _targetClients[key] = client;
            return client;
        }
        finally
        {
            _targetGate.Release();
        }
    }

    private static string? ExtractDeviceId(string topic)
    {
        var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 4 && parts[0].Equals("industrial-iot", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("devices", StringComparison.OrdinalIgnoreCase))
        {
            return parts[2];
        }
        if (parts.Length == 3 && parts[0].Equals("machines", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("telemetry", StringComparison.OrdinalIgnoreCase))
        {
            return parts[2];
        }
        return null;
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _source?.Dispose(); } catch { }
        foreach (var client in _targetClients.Values)
        {
            try { client.Dispose(); } catch { }
        }
        _targetGate.Dispose();
        base.Dispose();
    }
}
