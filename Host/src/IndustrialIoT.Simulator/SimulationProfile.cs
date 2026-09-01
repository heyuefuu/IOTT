namespace IndustrialIoT.Simulator;

using IndustrialIoT.Domain.Enums;

public class SimulationProfile
{
    public DeviceType DeviceType { get; set; } = DeviceType.CNC;
    public int MinLatencyMs { get; set; } = 5;
    public int MaxLatencyMs { get; set; } = 50;
    public double FailureProbability { get; set; } = 0.0; // 0 = never fail
    public string Scenario { get; set; } = "Normal";

    public static SimulationProfile Default => new();

    public static SimulationProfile CNC => new() { DeviceType = DeviceType.CNC };
    public static SimulationProfile PLC => new() { DeviceType = DeviceType.PLC, MinLatencyMs = 2, MaxLatencyMs = 20 };
    public static SimulationProfile Robot => new() { DeviceType = DeviceType.Robot, MinLatencyMs = 10, MaxLatencyMs = 60 };
    public static SimulationProfile HighLatency => new() { MinLatencyMs = 200, MaxLatencyMs = 500 };
    public static SimulationProfile IntermittentFault => new() { FailureProbability = 0.05 };
}
