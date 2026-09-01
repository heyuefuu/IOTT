namespace IndustrialIoT.Protocols.NCLink;

using System.Threading.Channels;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// NC-Link Sample 数据订阅器。
/// 将 NCLinkDriver 收到的 Sample/{guid}/{channelId} 推送数据转换为 CollectedDataBatch，
/// 写入 Channel 供 DataOutputDispatcher 消费。
/// </summary>
public sealed class NCLinkSampleSubscriber : IAsyncDisposable
{
    private readonly ILogger<NCLinkSampleSubscriber> _logger;
    private readonly Channel<CollectedDataBatch> _outputChannel;
    private NCLinkDriver? _driver;
    private string _deviceId = "";

    public NCLinkSampleSubscriber(ILogger<NCLinkSampleSubscriber> logger)
    {
        _logger = logger;
        _outputChannel = Channel.CreateBounded<CollectedDataBatch>(
            new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            });
    }

    /// <summary>获取输出 Channel 的 Reader，供外部消费。</summary>
    public ChannelReader<CollectedDataBatch> Output => _outputChannel.Reader;

    /// <summary>绑定 NCLinkDriver，开始接收 Sample 数据。</summary>
    public void Bind(NCLinkDriver driver, string deviceId)
    {
        _driver = driver;
        _deviceId = deviceId;
        _driver.OnSampleData(OnSampleDataReceived);
        _logger.LogInformation("NCLinkSampleSubscriber bound to device {DeviceId}", deviceId);
    }

    /// <summary>解绑。</summary>
    public void Unbind()
    {
        _driver = null;
        _deviceId = "";
    }

    private void OnSampleDataReceived(string channelId, IReadOnlyList<QueryResultValue> values)
    {
        try
        {
            var tagValues = values.Select(v => new TagValue
            {
                Address = _driver?.MapDevicePathToStandardPath(v.Id) ?? v.Id,
                DataType = InferDataType(v.Value),
                Value = ExtractValue(v.Value),
                Quality = v.Quality?.Equals("good", StringComparison.OrdinalIgnoreCase) != false
                    ? TagQuality.Good
                    : TagQuality.Bad,
                Timestamp = v.Timestamp.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(v.Timestamp.Value)
                    : DateTimeOffset.UtcNow,
            }).ToList();

            var batch = new CollectedDataBatch
            {
                DeviceId = _deviceId,
                GroupName = channelId,
                Values = tagValues,
                CollectedAt = DateTimeOffset.UtcNow,
            };

            // 非阻塞写入
            if (!_outputChannel.Writer.TryWrite(batch))
            {
                _logger.LogWarning("NCLink sample channel full, dropping batch for {DeviceId}/{Channel}",
                    _deviceId, channelId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process NC-Link sample data: {Channel}", channelId);
        }
    }

    private static DataType InferDataType(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null) return DataType.String;
        return node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => DataType.Bool,
            System.Text.Json.JsonValueKind.Number => DataType.Double,
            System.Text.Json.JsonValueKind.String => DataType.String,
            _ => DataType.String,
        };
    }

    private static object ExtractValue(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null) return "";
        return node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Number => node.GetValue<double>(),
            System.Text.Json.JsonValueKind.String => node.GetValue<string>(),
            _ => node.ToJsonString(),
        };
    }

    public ValueTask DisposeAsync()
    {
        Unbind();
        _outputChannel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
