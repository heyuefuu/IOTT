namespace IndustrialIoT.Infrastructure.Messaging;

using System.Text.Json;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

public sealed class MqttOutputOptions
{
    public const string SectionName = "DataOutput:Mqtt";

    public string BrokerHost { get; set; } = "localhost";
    public int BrokerPort { get; set; } = 1883;
    public string ClientId { get; set; } = $"industrial-iot-{Environment.MachineName}";
    public string TopicPattern { get; set; } = "industrial-iot/devices/{deviceId}/data";
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class MqttDataOutput : IDataOutput, IAsyncDisposable
{
    private readonly MqttOutputOptions _options;
    private readonly ILogger<MqttDataOutput> _logger;
    private readonly IMqttClient _client;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public string Name => "MQTT";

    public MqttDataOutput(IOptions<MqttOutputOptions> options, ILogger<MqttDataOutput> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new MqttClientFactory().CreateMqttClient();
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };

        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await ConnectAsync(ct);
        _logger.LogInformation("MQTT output initialized — broker {Host}:{Port}, clientId {ClientId}",
            _options.BrokerHost, _options.BrokerPort, _options.ClientId);
    }

    public async Task WriteAsync(CollectedDataBatch batch, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);

        var topic = _options.TopicPattern.Replace("{deviceId}", batch.DeviceId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(batch, _jsonOptions);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _client.PublishAsync(message, ct);

        _logger.LogDebug("MQTT published {Bytes} bytes to {Topic}", payload.Length, topic);
    }

    public Task FlushAsync(CancellationToken ct)
    {
        // MQTT publishes immediately; nothing to flush
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.DisconnectedAsync -= OnDisconnectedAsync;

        if (_client.IsConnected)
        {
            var disconnectOptions = new MqttClientDisconnectOptionsBuilder()
                .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                .Build();

            try
            {
                await _client.DisconnectAsync(disconnectOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during MQTT disconnect");
            }
        }

        _client.Dispose();
        _connectLock.Dispose();
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.BrokerHost, _options.BrokerPort)
            .WithClientId(_options.ClientId)
            .WithCleanSession(true);

        if (!string.IsNullOrEmpty(_options.Username))
        {
            optionsBuilder.WithCredentials(_options.Username, _options.Password);
        }

        var mqttOptions = optionsBuilder.Build();
        await _client.ConnectAsync(mqttOptions, ct);
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client.IsConnected)
            return;

        await _connectLock.WaitAsync(ct);
        try
        {
            if (_client.IsConnected)
                return;

            _logger.LogInformation("MQTT reconnecting to {Host}:{Port}", _options.BrokerHost, _options.BrokerPort);
            await ConnectAsync(ct);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        if (args.ClientWasConnected)
        {
            _logger.LogWarning("MQTT disconnected (reason: {Reason}). Will reconnect on next write.",
                args.Reason);
        }

        await Task.CompletedTask;
    }
}
