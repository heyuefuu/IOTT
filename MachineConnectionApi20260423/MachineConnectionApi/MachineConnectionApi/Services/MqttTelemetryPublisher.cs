using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MachineConnectionApi.Options;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MachineConnectionApi.Services;

public interface IMqttTelemetryPublisher
{
    /// <summary>将一批采集点以 JSON 发布到 MQTT（单条消息包含 deviceId、时间与全部点位）。</summary>
    Task PublishCollectionBatchAsync(
        string deviceId,
        DateTimeOffset collectedAt,
        IReadOnlyList<InfluxTelemetryPoint> points,
        CancellationToken ct = default);
}

public sealed class MqttTelemetryPublisher : IMqttTelemetryPublisher, IDisposable
{
    private readonly IOptionsMonitor<MqttOptions> _options;
    private readonly ILogger<MqttTelemetryPublisher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IMqttClient _client;
    private string? _connectFingerprint;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public MqttTelemetryPublisher(
        IOptionsMonitor<MqttOptions> options,
        ILogger<MqttTelemetryPublisher> logger)
    {
        _options = options;
        _logger = logger;
        _client = new MqttFactory().CreateMqttClient();
    }

    public async Task PublishCollectionBatchAsync(
        string deviceId,
        DateTimeOffset collectedAt,
        IReadOnlyList<InfluxTelemetryPoint> points,
        CancellationToken ct = default)
    {
        var opt = _options.CurrentValue;
        if (!opt.Enabled || points.Count == 0)
            return;

        if (string.IsNullOrWhiteSpace(opt.Host))
        {
            _logger.LogWarning("MQTT 已启用但 Host 为空，跳过发布");
            return;
        }

        var body = new MqttTelemetryBatchDto(
            deviceId,
            collectedAt,
            points.Select(static p => new MqttTelemetryPointDto(
                p.Name,
                p.Path,
                p.DataType,
                p.Value,
                p.Quality,
                p.Timestamp,
                p.Status,
                p.ErrorMessage)).ToList());

        var json = JsonSerializer.Serialize(body, JsonOptions);
        var payload = Encoding.UTF8.GetBytes(json);
        var topic = BuildTopic(opt.TopicPrefix, deviceId);
        var qos = ToQos(opt.QualityOfService);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                await EnsureConnectedAsync(opt, ct).ConfigureAwait(false);
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(qos)
                    .WithRetainFlag(false)
                    .Build();

                await _client.PublishAsync(message, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "MQTT 发布失败: deviceId={DeviceId}, topic={Topic}, qos={Qos}, payloadBytes={PayloadBytes}, pointCount={PointCount}",
                    deviceId,
                    topic,
                    (int)qos,
                    payload.Length,
                    points.Count);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureConnectedAsync(MqttOptions opt, CancellationToken ct)
    {
        var connectOptions = BuildConnectOptions(opt);
        var fingerprint = ConnectFingerprint(opt);
        if (_client.IsConnected && string.Equals(_connectFingerprint, fingerprint, StringComparison.Ordinal))
            return;

        if (_client.IsConnected)
            await _client.DisconnectAsync(cancellationToken: ct).ConfigureAwait(false);

        var timeout = TimeSpan.FromSeconds(Math.Max(3, opt.ConnectTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        await _client.ConnectAsync(connectOptions, linked.Token).ConfigureAwait(false);
        _connectFingerprint = fingerprint;
        _logger.LogInformation(
            "MQTT 已连接: {Host}:{Port}, TLS={Tls}, ClientId={ClientId}",
            opt.Host,
            opt.Port,
            opt.UseTls,
            opt.ClientId);
    }

    private static MqttClientOptions BuildConnectOptions(MqttOptions opt)
    {
        var clientId = string.IsNullOrWhiteSpace(opt.ClientId)
            ? "MachineConnectionApi"
            : opt.ClientId.Trim();

        var b = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithClientId(clientId);

        if (opt.UseTls)
            b = b.WithTlsOptions(o => o.UseTls());
        b = b.WithTcpServer(opt.Host.Trim(), opt.Port);

        if (!string.IsNullOrEmpty(opt.Username))
            b = b.WithCredentials(opt.Username, opt.Password ?? "");

        return b.Build();
    }

    private static string ConnectFingerprint(MqttOptions opt) =>
        $"{opt.Host}|{opt.Port}|{opt.UseTls}|{opt.Username}|{opt.ClientId}";

    private static MqttQualityOfServiceLevel ToQos(int q) =>
        q switch
        {
            2 => MqttQualityOfServiceLevel.ExactlyOnce,
            0 => MqttQualityOfServiceLevel.AtMostOnce,
            _ => MqttQualityOfServiceLevel.AtLeastOnce,
        };

    private static string BuildTopic(string prefix, string deviceId)
    {
        var p = (prefix ?? "machines/telemetry").Trim().TrimEnd('/');
        var id = SafeTopicSegment(deviceId);
        return $"{p}/{id}";
    }

    private static string SafeTopicSegment(string deviceId)
    {
        var s = (deviceId ?? "").Trim();
        if (s.Length == 0) return "_";
        Span<char> buffer = stackalloc char[Math.Min(s.Length, 160)];
        var n = 0;
        foreach (var ch in s)
        {
            if (n >= buffer.Length) break;
            buffer[n++] = ch is '/' or '\\' or '+' or '#' or ' ' or '\t' ? '_' : ch;
        }

        return new string(buffer[..n]);
    }

    public void Dispose()
    {
        try
        {
            // 应用关停路径上的同步等待需设上限，broker 无响应时不能卡住整个进程退出
            if (_client.IsConnected &&
                !_client.DisconnectAsync().Wait(TimeSpan.FromSeconds(3)))
                _logger.LogDebug("MQTT 断开超时（3s），直接释放客户端");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MQTT 断开时异常（可忽略）");
        }

        _client.Dispose();
        _gate.Dispose();
    }
}

internal sealed record MqttTelemetryBatchDto(
    string DeviceId,
    DateTimeOffset CollectedAt,
    IReadOnlyList<MqttTelemetryPointDto> Points);

internal sealed record MqttTelemetryPointDto(
    string Name,
    string Path,
    string DataType,
    object? Value,
    string Quality,
    string Timestamp,
    string Status,
    string? ErrorMessage);
