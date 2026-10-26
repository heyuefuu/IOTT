namespace DeviceSimGateway.Core;

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

/// <summary>
/// 虚拟第三方应用（MQTT 订阅端）：按《第三方数据同步接口文档》契约，
/// 作为普通 MQTT 客户端连接我方 Broker，订阅出站主题并按文档解析/校验/统计：
///   - topic `industrial-iot/devices/{deviceId}/data`（可配）
///   - payload {deviceId, groupName, values[{address,dataType,value,quality,timestamp,errorMessage}], collectedAt, collectionDuration}
///   - quality 数字枚举 0=Good/1=Bad/2=Uncertain（也兼容字符串）
///   - 用 deviceId + address + timestamp 去重（QoS1 可能重复投递）
/// 仅用于联调"第三方侧连通性/契约一致性"，不参与设备协议栈。
/// </summary>
public sealed class ThirdPartySubscriber
{
    private const int MaxRecent = 20;
    private const int MaxDedupeKeys = 50000;

    private readonly ILogger _log;
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, byte> _dedupe = new();
    private readonly ConcurrentDictionary<string, byte> _devices = new();
    private readonly Queue<JsonObject> _recent = new();

    private ThirdPartySubscriberConfig _cfg = new();
    private CancellationToken _appStopping = CancellationToken.None;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IMqttClient? _client;

    private bool _connected;
    private long _received, _points, _badQuality, _deduped, _parseErrors;
    private DateTimeOffset? _lastMessageAt;
    private string? _lastError;

    public ThirdPartySubscriber(ILogger log) => _log = log;

    public ThirdPartySubscriberConfig Config { get { lock (_sync) return _cfg; } }

    public bool IsConnected { get { lock (_sync) return _connected; } }

    /// <summary>应用新配置（含启用/停用）并重建连接。</summary>
    public void Apply(ThirdPartySubscriberConfig cfg)
    {
        lock (_sync) _cfg = cfg ?? new ThirdPartySubscriberConfig();
        Restart();
    }

    public void Start(CancellationToken appStopping)
    {
        _appStopping = appStopping;
        Restart();
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
        var client = _client;
        _client = null;
        try { if (client is not null) { client.DisconnectAsync().Wait(TimeSpan.FromSeconds(2)); } } catch { }
        lock (_sync) _connected = false;
    }

    private void Restart()
    {
        Stop();
        var cfg = Config;
        if (!cfg.Enabled)
        {
            _log.LogInformation("第三方订阅模拟：未启用");
            return;
        }
        if (string.IsNullOrWhiteSpace(cfg.BrokerHost) || cfg.BrokerPort is <= 0 or > 65535)
        {
            SetError("Broker 地址/端口无效");
            return;
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(_appStopping);
        var ct = _cts.Token;
        _loop = Task.Run(() => LoopAsync(cfg, ct), CancellationToken.None);
    }

    private async Task LoopAsync(ThirdPartySubscriberConfig cfg, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var factory = new MqttClientFactory();
                var client = factory.CreateMqttClient();
                client.ApplicationMessageReceivedAsync += OnMessageAsync;
                client.DisconnectedAsync += e =>
                {
                    lock (_sync) _connected = false;
                    SetError($"disconnected: {e.Reason}");
                    return Task.CompletedTask;
                };

                var b = new MqttClientOptionsBuilder()
                    .WithProtocolVersion(MqttProtocolVersion.V311)
                    .WithClientId(string.IsNullOrWhiteSpace(cfg.ClientId) ? "third-party-sim" : cfg.ClientId.Trim())
                    .WithTcpServer(cfg.BrokerHost.Trim(), cfg.BrokerPort);
                if (cfg.UseTls) b = b.WithTlsOptions(o => o.UseTls());
                if (!string.IsNullOrWhiteSpace(cfg.Username)) b = b.WithCredentials(cfg.Username, cfg.Password ?? "");

                _client = client;
                await client.ConnectAsync(b.Build(), ct).ConfigureAwait(false);
                await client.SubscribeAsync(
                    new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(cfg.TopicFilter, MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build(), ct).ConfigureAwait(false);
                lock (_sync) { _connected = true; _lastError = null; }
                _log.LogInformation("第三方订阅模拟已连接 {Host}:{Port}，订阅 {Filter}", cfg.BrokerHost, cfg.BrokerPort, cfg.TopicFilter);
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                lock (_sync) _connected = false;
                SetError(ex.Message);
                _log.LogWarning("第三方订阅模拟连接失败：{Message}（3s 后重试）", ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = e.ApplicationMessage.ConvertPayloadToString();
            JsonNode? node;
            try { node = JsonNode.Parse(payload); }
            catch { Interlocked.Increment(ref _parseErrors); SetError("payload 非合法 JSON"); return Task.CompletedTask; }

            if (node is not JsonObject obj) { Interlocked.Increment(ref _parseErrors); return Task.CompletedTask; }

            var deviceId = obj["deviceId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(deviceId)) deviceId = DeviceIdFromTopic(topic);
            var groupName = obj["groupName"]?.GetValue<string>();
            var values = obj["values"] as JsonArray;

            var newPoints = 0; var bad = 0; var dedup = 0;
            if (values is not null)
            {
                foreach (var item in values.OfType<JsonObject>())
                {
                    var address = item["address"]?.ToString() ?? "";
                    var ts = item["timestamp"]?.ToString() ?? "";
                    if (ReadQuality(item["quality"]) != 0) bad++;
                    var key = $"{deviceId}|{address}|{ts}";
                    if (_dedupe.TryAdd(key, 0)) newPoints++;
                    else dedup++;
                }
            }

            if (_dedupe.Count > MaxDedupeKeys) _dedupe.Clear();

            var sample = new JsonObject
            {
                ["receivedAt"] = DateTimeOffset.Now.ToString("HH:mm:ss.fff"),
                ["topic"] = topic,
                ["deviceId"] = deviceId,
                ["groupName"] = groupName,
                ["points"] = values?.Count ?? 0,
                ["badQuality"] = bad,
                ["deduped"] = dedup,
                ["collectedAt"] = obj["collectedAt"]?.ToString(),
                ["collectionDuration"] = obj["collectionDuration"]?.ToString(),
            };

            lock (_sync)
            {
                _received++;
                _points += newPoints;
                _badQuality += bad;
                _deduped += dedup;
                _lastMessageAt = DateTimeOffset.Now;
                _lastError = null;
                if (!string.IsNullOrEmpty(deviceId)) _devices[deviceId!] = 0;
                _recent.Enqueue(sample);
                while (_recent.Count > MaxRecent) _recent.Dequeue();
            }
        }
        catch (Exception ex) { Interlocked.Increment(ref _parseErrors); SetError(ex.Message); }
        return Task.CompletedTask;
    }

    private static int ReadQuality(JsonNode? q)
    {
        if (q is null) return 0;
        if (int.TryParse(q.ToString(), out var n)) return n;
        return q.ToString().Trim().ToLowerInvariant() switch
        {
            "bad" => 1,
            "uncertain" => 2,
            _ => 0,
        };
    }

    private static string DeviceIdFromTopic(string topic)
    {
        // 形如 industrial-iot/devices/{deviceId}/data
        var parts = topic.Split('/');
        for (var i = 0; i < parts.Length - 1; i++)
            if (string.Equals(parts[i], "devices", StringComparison.OrdinalIgnoreCase)) return parts[i + 1];
        return parts.Length >= 2 ? parts[^2] : topic;
    }

    private void SetError(string message) { lock (_sync) _lastError = message; }

    public object GetStatus()
    {
        var cfg = Config;
        lock (_sync)
        {
            return new
            {
                enabled = cfg.Enabled,
                isConnected = _connected,
                broker = $"{cfg.BrokerHost}:{cfg.BrokerPort}",
                topicFilter = cfg.TopicFilter,
                clientId = cfg.ClientId,
                receivedMessages = _received,
                points = _points,
                devices = _devices.Count,
                badQualityPoints = _badQuality,
                dedupedPoints = _deduped,
                parseErrors = _parseErrors,
                lastMessageAt = _lastMessageAt,
                lastError = _lastError,
                recent = _recent.ToArray(),
            };
        }
    }
}
