namespace IndustrialIoT.Protocols.Gsk;

using System.Runtime.InteropServices;

/// <summary>
/// Minimal DTOs surfaced across the <see cref="IGskrmApi"/> seam. Struct
/// shapes below are the **narrow public view** we hand to drivers; the real
/// P/Invoke signatures may deal with different marshalling layouts internally
/// (see NativeGskrmApi.*.cs). Keep this file free of `[DllImport]`.
/// </summary>
public sealed record GskrmCncInfo
{
    public required string MachineType { get; init; }
    public required string SeriesLabel { get; init; }
    public required int PathCount { get; init; }
    public required int AxisCount { get; init; }
    public string? Version { get; init; }
}

public sealed record GskrmAxisInfo
{
    public required int AxisIndex { get; init; }
    public required string Name { get; init; }
    public required string Unit { get; init; }
}

public sealed record GskrmPosition
{
    public required double Absolute { get; init; }
    public required double Machine { get; init; }
    public required double Relative { get; init; }
    public double Distance { get; init; }
}

public sealed record GskrmRateInfo
{
    public required int FeedRate { get; init; }
    public required int FastRate { get; init; }
    public required int JogRate { get; init; }
    public required int SpindleRate { get; init; }
    public required int HandWheelRate { get; init; }
}

public sealed record GskrmAlarm
{
    public required int Code { get; init; }
    public required string Message { get; init; }
    public required string Source { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public sealed record GskrmCncFileEntry
{
    public required string Name { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public string? Attribute { get; init; }
}

/// <summary>Native `GSKRM_GetCncState` bitmap — human-friendly projection.</summary>
public sealed record GskrmCncState
{
    public required int RawState { get; init; }
    public bool IsRunning { get; init; }
    public bool IsAlarm { get; init; }
    public bool EmergencyStop { get; init; }
    public string? WorkMode { get; init; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct GskrmPositionRaw
{
    public double Absolute;
    public double Machine;
    public double Relative;
    public double Distance;
}
