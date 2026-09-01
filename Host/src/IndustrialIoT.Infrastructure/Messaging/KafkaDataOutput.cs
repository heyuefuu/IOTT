namespace IndustrialIoT.Infrastructure.Messaging;

using System.Text.Json;
using Confluent.Kafka;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class KafkaOutputOptions
{
    public const string SectionName = "DataOutput:Kafka";

    public bool Enabled { get; set; }
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string Topic { get; set; } = "industrial-iot-data";
    public Acks Acks { get; set; } = Acks.Leader;
    public int LingerMs { get; set; } = 5;
    public int BatchSize { get; set; } = 65536;
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }
}

public sealed class KafkaDataOutput : IDataOutput, IDisposable
{
    private readonly KafkaOutputOptions _options;
    private readonly ILogger<KafkaDataOutput> _logger;
    private readonly IProducer<string, string> _producer;
    private readonly JsonSerializerOptions _jsonOptions;

    public string Name => "Kafka";

    public KafkaDataOutput(IOptions<KafkaOutputOptions> options, ILogger<KafkaDataOutput> logger)
    {
        _options = options.Value;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };

        var config = new ProducerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            Acks = Acks.All,
            LingerMs = _options.LingerMs,
            BatchSize = _options.BatchSize,
            EnableIdempotence = true,
            MessageSendMaxRetries = 3,
        };

        if (!string.IsNullOrEmpty(_options.SaslUsername))
        {
            config.SecurityProtocol = SecurityProtocol.SaslPlaintext;
            config.SaslMechanism = SaslMechanism.Plain;
            config.SaslUsername = _options.SaslUsername;
            config.SaslPassword = _options.SaslPassword;
        }

        _producer = new ProducerBuilder<string, string>(config)
            .SetErrorHandler((_, error) =>
            {
                _logger.LogError("Kafka producer error: {Reason} (code: {Code}, isFatal: {Fatal})",
                    error.Reason, error.Code, error.IsFatal);
            })
            .SetLogHandler((_, log) =>
            {
                _logger.LogDebug("Kafka internal: [{Level}] {Message}", log.Level, log.Message);
            })
            .Build();
    }

    public Task InitializeAsync(CancellationToken ct)
    {
        _logger.LogInformation("Kafka output initialized — servers {Servers}, topic {Topic}",
            _options.BootstrapServers, _options.Topic);
        return Task.CompletedTask;
    }

    public async Task WriteAsync(CollectedDataBatch batch, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(batch, _jsonOptions);
        var message = new Message<string, string>
        {
            Key = batch.DeviceId,
            Value = json,
        };

        try
        {
            var result = await _producer.ProduceAsync(_options.Topic, message, ct);

            _logger.LogDebug("Kafka produced to {Topic}[{Partition}]@{Offset}, key={Key}",
                result.Topic, result.Partition.Value, result.Offset.Value, batch.DeviceId);
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex, "Kafka produce failed for device {DeviceId}: {Reason}",
                batch.DeviceId, ex.Error.Reason);
            throw;
        }
    }

    public Task FlushAsync(CancellationToken ct)
    {
        _producer.Flush(ct);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try
        {
            _producer.Flush(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error flushing Kafka producer during dispose");
        }

        _producer.Dispose();
    }
}
