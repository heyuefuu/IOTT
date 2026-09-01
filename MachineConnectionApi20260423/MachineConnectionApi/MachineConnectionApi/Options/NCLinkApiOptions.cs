namespace MachineConnectionApi.Options;

/// <summary>
/// 华中数控 NC-Link API Server（Spring Boot 应用，默认端口 19001）的连接参数。
/// 上游契约：POST /v1/{deviceId}/data/，请求体 {operation, items[]}；详见手册第 5 章。
/// </summary>
public class NCLinkApiOptions
{
    public const string SectionName = "NCLinkApi";

    /// <summary>关闭时按 NCLinkApi 协议采集的请求将直接报错（不静默跳过，便于排查）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>API Server 基地址（含协议、主机、端口），如 http://127.0.0.1:19001。</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:19001";

    /// <summary>HTTP 请求超时（秒）。读多点位时建议放宽，机床上报有延迟。</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>单个 item 请求的默认 timeout（毫秒，传给 nclink-api-server 的 timeout 字段）。</summary>
    public int DefaultItemTimeoutMs { get; set; } = 3000;

    /// <summary>NC-Link device model REST path template: GET /v1/{deviceId}/model.</summary>
    public string ModelPathTemplate { get; set; } = "v1/{deviceId}/model";
}
