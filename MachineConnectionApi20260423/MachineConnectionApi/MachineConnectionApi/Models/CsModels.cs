namespace MachineConnectionApi.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Client/Server 通讯验证模型集合。
/// 三类对象对应前端三页：网关（客户端连接目标）、数据源（客户端周期探测）、服务端服务（软件作为 server 监听）。
/// </summary>

/// <summary>网关定义：客户端模式下软件主动连接的目标端点。</summary>
public class CsGateway
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>目标主机/IP。</summary>
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    /// <summary>协议标签：Modbus / OPCUA / MQTT / REST。当前底层统一以 TCP 连通性验证。</summary>
    public string Type { get; set; } = "Modbus";
    /// <summary>FTP 登录用户名（type=FTP 时使用，留空为匿名）。</summary>
    public string? Username { get; set; }
    /// <summary>FTP 登录密码（type=FTP 时使用）。write-only：接收前端提交，不随响应回传。</summary>
    [JsonIgnore]
    public string? Password { get; set; }

    /// <summary>仅用于反序列化接收前端提交的密码，无 getter 故不会被序列化输出。</summary>
    [JsonPropertyName("password")]
    public string? PasswordInput { set => Password = value; }
    /// <summary>FTP 验证的远程目录（可选，默认根目录 /）。</summary>
    public string? RemotePath { get; set; }
    public string? Description { get; set; }
    /// <summary>运行状态：运行中 / 停止（由最近一次探测结果决定）。</summary>
    public string Status { get; set; } = "停止";
    /// <summary>最后心跳（最近一次成功探测时间）。</summary>
    public string? LastHeartbeat { get; set; }
}

/// <summary>客户端数据源：启用后按 UpdateInterval 周期探测所关联网关的连通性。</summary>
public class CsDataSource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Modbus";
    /// <summary>关联的网关 Id（探测目标来源）。</summary>
    public string GatewayId { get; set; } = "";
    /// <summary>自由配置（如 Modbus 寄存器、OPC UA NodeId 等），当前仅作记录。</summary>
    public string? Config { get; set; }
    /// <summary>探测周期（秒）。</summary>
    public int UpdateInterval { get; set; } = 5;
    /// <summary>状态：启用 / 禁用。</summary>
    public string Status { get; set; } = "禁用";
    /// <summary>最近一次更新时间。</summary>
    public string? LastUpdate { get; set; }
    /// <summary>最近一次探测结果摘要（如 "OK 12.3ms" 或失败原因）。</summary>
    public string? LastResult { get; set; }
}

/// <summary>服务端服务：软件作为 TCP server 监听端口，等待外部客户端/设备连入。</summary>
public class CsServerService
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>服务类型标签：ModbusServer / OPCUAServer / MQTTBroker / RESTServer / FtpServer。</summary>
    public string Type { get; set; } = "ModbusServer";
    /// <summary>FtpServer 模式要求的登录用户名（必须非空，默认禁止匿名）。</summary>
    public string? Username { get; set; }
    /// <summary>FtpServer 模式要求的登录密码。write-only：接收前端提交，不随响应回传。</summary>
    [JsonIgnore]
    public string? Password { get; set; }

    /// <summary>仅用于反序列化接收前端提交的密码，无 getter 故不会被序列化输出。</summary>
    [JsonPropertyName("password")]
    public string? PasswordInput { set => Password = value; }
    public int Port { get; set; }
    public string? Description { get; set; }
    public int MaxClients { get; set; } = 100;
    /// <summary>运行状态：运行中 / 停止。</summary>
    public string Status { get; set; } = "停止";
    /// <summary>当前连入的客户端数量（实时）。</summary>
    public int ClientCount { get; set; }
    /// <summary>最近一次客户端接入时间。</summary>
    public string? LastAccess { get; set; }
}

/// <summary>服务端当前的一条客户端连接。</summary>
public record CsServerConnection(string RemoteEndpoint, string ConnectedAt, long BytesReceived);

/// <summary>客户端 TCP 探测结果。</summary>
public record CsProbeResult(bool Success, double RttMs, string Message, string Timestamp);

/// <summary>并发连接压测请求：TCP 自 StartIp 起递增目标；MQTT 将 StartIp 作为 Broker Host。</summary>
public record CsParallelTestRequest(
    string StartIp, int Port, int DeviceCount, int ConcurrentCount, int TimeoutMs, int HoldMs = 0,
    string Protocol = "TCP", bool MqttUseTls = false, string? MqttUsername = null,
    string? MqttPassword = null, string? MqttClientId = null);

/// <summary>同一目标并发连接压测请求：对 Host:Port 发起 ConnectionCount 路真实 TCP 建连，ConcurrentCount 控制同时在途连接数。</summary>
public record CsSameTargetParallelTestRequest(
    string Host, int Port, int ConnectionCount, int ConcurrentCount, int TimeoutMs);

/// <summary>并发压测中的一条失败明细。</summary>
public record CsParallelFailure(string DeviceIp, string Error, string Time);

/// <summary>并发连接压测汇总结果。</summary>
public record CsParallelTestResult(
    int Total, int Success, int Failure, int SuccessRate,
    double AvgRttMs, double MaxRttMs,
    IReadOnlyList<CsParallelFailure> Failures, string FinishedAt);

/// <summary>并发连接压测报告导出请求。</summary>
public record CsParallelReportRequest(
    CsParallelTestRequest Request, CsParallelTestResult Result,
    string? ConnectionMode = null, int DurationSeconds = 0, string? GeneratedAt = null);

/// <summary>报告文件输出。</summary>
public record CsReportFile(string FileName, string ContentType, byte[] Content);
