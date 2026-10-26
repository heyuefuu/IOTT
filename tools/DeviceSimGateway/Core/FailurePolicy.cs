namespace DeviceSimGateway.Core;

/// <summary>
/// 随机失败注入策略：让各协议服务按概率拒绝/异常响应，用于联调主机侧的稳定性、中断与重试逻辑。
/// 概率为 0–100 的百分比；可选择“断链”（直接关闭连接，模拟丢包/掉线）或“设备错误”（返回协议错误/坏质量）。
/// </summary>
public sealed class FailurePolicy
{
    private readonly object _sync = new();
    private readonly Random _rng = new();

    public bool Enabled { get; private set; }
    public double RatePercent { get; private set; }
    /// <summary>true=断链（不响应并关闭连接）；false=设备错误（返回协议错误/坏质量）。</summary>
    public bool DropConnection { get; private set; } = true;

    public void Apply(bool enabled, double ratePercent, bool dropConnection)
    {
        lock (_sync)
        {
            Enabled = enabled;
            RatePercent = double.IsFinite(ratePercent) ? Math.Clamp(ratePercent, 0, 100) : 0;
            DropConnection = dropConnection;
        }
    }

    public (bool Enabled, double RatePercent, bool DropConnection) Snapshot()
    {
        lock (_sync) return (Enabled, RatePercent, DropConnection);
    }

    /// <summary>本次请求是否注入失败。</summary>
    public bool ShouldFail()
    {
        lock (_sync)
        {
            if (!Enabled || RatePercent <= 0) return false;
            if (RatePercent >= 100) return true;
            return _rng.NextDouble() * 100.0 < RatePercent;
        }
    }
}
