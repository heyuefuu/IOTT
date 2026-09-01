namespace MachineConnectionApi.Options;

/// <summary>将采集批次发布到第三方 MQTT Broker（与界面「开始采集」每轮上报一致）。</summary>
public class MqttOptions
{
    public const string SectionName = "Mqtt";

    /// <summary>关闭时不连接、不发布。</summary>
    public bool Enabled { get; set; }

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 1883;

    /// <summary>客户端标识（Broker 内唯一即可）。</summary>
    public string ClientId { get; set; } = "MachineConnectionApi";

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>是否使用 TLS（MQTTS，端口常为 8883）。</summary>
    public bool UseTls { get; set; }

    /// <summary>
    /// 主题前缀，不含尾部斜杠；实际主题为 <c>{TopicPrefix}/{deviceId}</c>，deviceId 中的非法字符会替换为下划线。
    /// </summary>
    public string TopicPrefix { get; set; } = "machines/telemetry";

    /// <summary>QoS：0=最多一次，1=至少一次，2=恰好一次。</summary>
    public int QualityOfService { get; set; } = 1;

    /// <summary>连接超时（秒）。</summary>
    public int ConnectTimeoutSeconds { get; set; } = 10;
}
