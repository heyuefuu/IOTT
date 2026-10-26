namespace DeviceSimGateway.Core;

public enum ServiceState { Stopped, Running, Error, NotImplemented }

/// <summary>单个协议服务的可持久化配置。</summary>
public sealed class SimServiceConfig
{
    public string Key { get; set; } = "";
    public int Port { get; set; }
    public bool Enabled { get; set; }
    public Dictionary<string, string> Options { get; set; } = new();
}

public enum RegMode { Constant, Ramp, Sine, Random, Counter }

public sealed class RegPattern
{
    public RegMode Mode { get; set; } = RegMode.Sine;
    public double Base { get; set; } = 1000;
    public double Amplitude { get; set; } = 500;
    public double PeriodMs { get; set; } = 10000;
    public double Step { get; set; } = 1;
    public double Min { get; set; } = 0;
    public double Max { get; set; } = 65535;
}

public enum CoilMode { Constant, Toggle, Random }

public sealed class CoilPattern
{
    public CoilMode Mode { get; set; } = CoilMode.Toggle;
    public double PeriodMs { get; set; } = 2000;
    public double Density { get; set; } = 0.5;
}

public sealed class GeneratorConfig
{
    public int HoldingSize { get; set; } = 2000;
    public int InputSize { get; set; } = 2000;
    public int CoilCount { get; set; } = 2000;
    public int DiscreteCount { get; set; } = 2000;
    public int IntervalMs { get; set; } = 500;

    /// <summary>保持寄存器初始种子（上电填一次；默认之后由客户端读写，不被生成器覆盖）。</summary>
    public RegPattern HoldingSeed { get; set; } = new() { Mode = RegMode.Ramp, Base = 0, Step = 1, Amplitude = 1, PeriodMs = 10000 };
    /// <summary>输入寄存器（生成器持续驱动）。</summary>
    public RegPattern Input { get; set; } = new() { Mode = RegMode.Sine, Base = 1000, Amplitude = 500, PeriodMs = 10000 };
    /// <summary>线圈（生成器驱动一部分，其余由客户端读写）。</summary>
    public CoilPattern Coils { get; set; } = new() { Mode = CoilMode.Toggle, PeriodMs = 2000, Density = 0.5 };
    /// <summary>离散输入（只读，生成器驱动）。</summary>
    public CoilPattern Discretes { get; set; } = new() { Mode = CoilMode.Toggle, PeriodMs = 1500, Density = 0.3 };
    /// <summary>是否让生成器也驱动保持寄存器（默认 false，保留客户端写入）。</summary>
    public bool DriveHolding { get; set; }

    public GeneratorConfig Clone() => new()
    {
        HoldingSize = HoldingSize, InputSize = InputSize, CoilCount = CoilCount, DiscreteCount = DiscreteCount,
        IntervalMs = IntervalMs,
        HoldingSeed = new() { Mode = HoldingSeed.Mode, Base = HoldingSeed.Base, Amplitude = HoldingSeed.Amplitude, PeriodMs = HoldingSeed.PeriodMs, Step = HoldingSeed.Step, Min = HoldingSeed.Min, Max = HoldingSeed.Max },
        Input = new() { Mode = Input.Mode, Base = Input.Base, Amplitude = Input.Amplitude, PeriodMs = Input.PeriodMs, Step = Input.Step, Min = Input.Min, Max = Input.Max },
        Coils = new() { Mode = Coils.Mode, PeriodMs = Coils.PeriodMs, Density = Coils.Density },
        Discretes = new() { Mode = Discretes.Mode, PeriodMs = Discretes.PeriodMs, Density = Discretes.Density },
        DriveHolding = DriveHolding,
    };
}

/// <summary>虚拟第三方应用（MQTT 订阅端）配置：按《第三方数据同步接口文档》订阅我方出站主题。</summary>
public sealed class ThirdPartySubscriberConfig
{
    public bool Enabled { get; set; }
    public string BrokerHost { get; set; } = "127.0.0.1";
    public int BrokerPort { get; set; } = 1883;
    /// <summary>订阅过滤器；文档口径为 industrial-iot/devices/+/data。</summary>
    public string TopicFilter { get; set; } = "industrial-iot/devices/+/data";
    public string ClientId { get; set; } = "third-party-sim";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseTls { get; set; }
}

public sealed class GatewayConfig
{
    public int MgmtPort { get; set; } = 8090;
    public List<SimServiceConfig> Services { get; set; } = new();
    public GeneratorConfig Generator { get; set; } = new();

    /// <summary>随机失败注入：是否启用。</summary>
    public bool FailureEnabled { get; set; }
    /// <summary>随机失败概率（0–100%）。</summary>
    public double FailureRatePercent { get; set; }
    /// <summary>true=断链，false=返回协议错误/坏质量。</summary>
    public bool FailureDropConnection { get; set; } = true;

    /// <summary>虚拟第三方应用（MQTT 订阅端）配置。</summary>
    public ThirdPartySubscriberConfig ThirdParty { get; set; } = new();
}

public sealed record ServiceRuntime(
    string Key, string Name, string Description, bool Implemented,
    int Port, bool Enabled, ServiceState State, string Message);
