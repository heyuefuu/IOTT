namespace IndustrialIoT.Protocols.NCLinkApi;

/// <summary>
/// NC-Link API Server 状态码 — 严格对照《NC-Link应用开发指导手册》附录二表 9-2。
/// </summary>
public enum NCLinkApiStatusCode
{
    Success = 0,
    RequestErrNullItem = 1,
    RequestErrOperation = 2,
    RequestErrPath = 3,
    RequestErrIndex = 4,
    RequestErrKey = 5,
    RequestErrTimeout = 6,
    RequestErrSubscribe = 7,
    RequestErrModel = 8,
    RequestErrOther = 9,
    DeviceErrConnection = 10,
    DeviceErrOther = 11,
    ExecuteErrDevice = 12,
    ExecuteErrRequest = 13,
    ExecuteErrExecute = 14,
    ExecuteErrInterrupt = 15,
    ExecuteErrTimeout = 16,
    ExecuteErrSubId = 17,
    ExecuteErrTopic = 18,
    ExecuteErrOther = 19,
    ExecuteErrItems = 20,
}

public static class NCLinkApiStatusCodeExtensions
{
    public static string Describe(this NCLinkApiStatusCode code) => code switch
    {
        NCLinkApiStatusCode.Success => "成功",
        NCLinkApiStatusCode.RequestErrNullItem => "请求的数据项为空",
        NCLinkApiStatusCode.RequestErrOperation => "请求体的 operation 字段错误",
        NCLinkApiStatusCode.RequestErrPath => "请求体的 path 字段无效",
        NCLinkApiStatusCode.RequestErrIndex => "请求体的 index 字段无效",
        NCLinkApiStatusCode.RequestErrKey => "请求体的 key 字段无效",
        NCLinkApiStatusCode.RequestErrTimeout => "请求超时（API-Server 与 MQTT 之间）",
        NCLinkApiStatusCode.RequestErrSubscribe => "订阅数据错误",
        NCLinkApiStatusCode.RequestErrModel => "设备模型文件错误",
        NCLinkApiStatusCode.RequestErrOther => "其他错误",
        NCLinkApiStatusCode.DeviceErrConnection => "设备连接错误",
        NCLinkApiStatusCode.DeviceErrOther => "设备其他错误",
        NCLinkApiStatusCode.ExecuteErrDevice => "设备 SN 号异常（未连接或错误）",
        NCLinkApiStatusCode.ExecuteErrRequest => "文件大小错误",
        NCLinkApiStatusCode.ExecuteErrExecute => "执行获取数据请求异常",
        NCLinkApiStatusCode.ExecuteErrInterrupt => "执行中断异常",
        NCLinkApiStatusCode.ExecuteErrTimeout => "执行超时",
        NCLinkApiStatusCode.ExecuteErrSubId => "采样通道错误",
        NCLinkApiStatusCode.ExecuteErrTopic => "Topic 错误",
        NCLinkApiStatusCode.ExecuteErrOther => "其他错误",
        NCLinkApiStatusCode.ExecuteErrItems => "执行获取/设置数据异常",
        _ => $"未知状态码 ({(int)code})",
    };

    public static bool IsSuccess(this NCLinkApiStatusCode code) => code == NCLinkApiStatusCode.Success;
}
