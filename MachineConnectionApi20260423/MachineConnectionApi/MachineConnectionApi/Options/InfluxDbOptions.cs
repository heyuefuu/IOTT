namespace MachineConnectionApi.Options;

public class InfluxDbOptions
{
    public const string SectionName = "InfluxDB";

    /// <summary>关闭时不创建客户端、不写库。</summary>
    public bool Enabled { get; set; }

    public string Url { get; set; } = "http://localhost:8086";

    public string Token { get; set; } = "";

    public string Org { get; set; } = "";

    public string Bucket { get; set; } = "machine_telemetry";

    /// <summary>Influx measurement 名称（表名概念）。</summary>
    public string Measurement { get; set; } = "datapoint";

    /// <summary>写入请求超时时间（秒）。</summary>
    public int WriteTimeoutSeconds { get; set; } = 15;

    /// <summary>写入失败后的重试次数（不含首次）。</summary>
    public int WriteRetryCount { get; set; } = 2;

    /// <summary>重试前等待毫秒数。</summary>
    public int WriteRetryDelayMs { get; set; } = 500;
}
