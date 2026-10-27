namespace IndustrialIoT.Protocols.Inovance;

using System.Globalization;
using HslCommunication.Profinet.Inovance;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Addressing;

public static class InovanceAddressSpaceProfile
{
    public static IAddressSpaceBrowser Create(DeviceConnectionConfig config, string? model = null)
    {
        var series = InovanceAddressSpace.ParseSeries(InovanceAddressSpace.GetProperty(config, "Series"));
        if (series is not (InovanceSeries.AM or InovanceSeries.H3U or InovanceSeries.H5U or InovanceSeries.Easy))
            throw new AddressSpaceConfigurationException("地址范围浏览需要明确的汇川 Series：H3U、H5U、AM 或 Easy。");
        var areas = series == InovanceSeries.AM ? AmAreas() : SmallPlcAreas(series.Value);
        // Series alone does not identify the installed CPU's memory capacity or I/O mapping.
        var selected = PlcAddressRangeBrowser.Configure(areas, config, requireExplicitRanges: true);
        try
        {
            foreach (var area in selected)
            {
                InovanceAddressSpace.Normalize(area.Address(area.Start), series.Value);
                InovanceAddressSpace.Normalize(area.Address(area.End), series.Value);
            }
        }
        catch (FormatException error) { throw new AddressSpaceConfigurationException(error.Message); }
        return new PlcAddressRangeBrowser(selected, "ConfiguredRange",
            $"目录来自 {series} 的配置范围，未在线发现变量；X/Y 使用八进制编号，AM 位使用字节.位，权限及用途以现场为准。");
    }

    private static IReadOnlyList<PlcAddressArea> SmallPlcAreas(InovanceSeries series)
    {
        var areas = new List<PlcAddressArea> { Scalar("D", DataType.UInt16), Scalar("R", DataType.UInt16) };
        var flags = series == InovanceSeries.H3U ? new[] { "M", "SM", "S", "T", "C" } : new[] { "M", "B", "S" };
        areas.AddRange(flags.Select(prefix => Scalar(prefix, DataType.Bool)));
        if (series == InovanceSeries.H3U) areas.Add(Scalar("SD", DataType.UInt16));
        areas.Add(new("X", "八进制输入位", 0, 65535, DataType.Bool, index => "X" + Convert.ToString(index, 8), false));
        areas.Add(new("Y", "八进制输出位", 0, 65535, DataType.Bool, index => "Y" + Convert.ToString(index, 8)));
        return areas;
    }

    private static IReadOnlyList<PlcAddressArea> AmAreas()
        => [Scalar("MW", DataType.UInt16), Scalar("MD", DataType.UInt32), Scalar("SD", DataType.UInt16),
            Scalar("SM", DataType.Bool), ByteBits("Q"), ByteBits("IX", false), ByteBits("MX")];

    private static PlcAddressArea Scalar(string prefix, DataType type)
        => new(prefix, $"{prefix} ({type})", 0, 65535, type, index => prefix + index.ToString(CultureInfo.InvariantCulture));

    private static PlcAddressArea ByteBits(string prefix, bool writable = true)
        => new(prefix, $"{prefix} 字节.位", 0, 65535, DataType.Bool,
            index => $"{prefix}{(index / 8).ToString(CultureInfo.InvariantCulture)}.{index % 8}", writable);
}
