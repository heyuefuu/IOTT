namespace IndustrialIoT.Protocols.Mewtocol;

using System.Globalization;
using System.Text.RegularExpressions;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Addressing;

public static class MewtocolAddressSpaceProfile
{
    public static IAddressSpaceBrowser Create(DeviceConnectionConfig config, string? model = null)
    {
        var cpu = (string.IsNullOrWhiteSpace(model) ? Get(config, "Model") : model)?.Trim().ToUpperInvariant() ?? "";
        var match = Regex.Match(cpu, @"^FP0R[- ]?(?<cpu>C10|C14|C16|C32|T32|F32)(?:[A-Z0-9-]*)$");
        var known = match.Success;
        var smallCpu = known && new[] { "C10", "C14", "C16" }.Contains(match.Groups["cpu"].Value);
        var configured = !string.IsNullOrWhiteSpace(Get(config, "AddressSpaceRanges"));
        var areas = new List<PlcAddressArea>
        {
            Word("DT", "数据寄存器", 0, known ? smallCpu ? 12314 : 32764 : 65535),
            Word("LD", "链接寄存器", 0, known ? 255 : 65535),
            Bits("X", "输入位", known ? 1759 : 65535, writable: false),
            Bits("Y", "输出位", known ? 1759 : 65535),
            Bits("R", "内部继电器位", known ? 4095 : 65535),
            Bits("L", "链接继电器位", known ? 2047 : 65535),
        };
        // Timer/counter partitioning is configurable in the PLC; do not guess its allocation.
        if (configured)
        {
            areas.Add(Word("T", "定时器（按现场分配配置）", 0, known ? 1023 : 65535));
            areas.Add(Word("C", "计数器（按现场分配配置）", 0, known ? 1023 : 65535));
        }
        var selected = PlcAddressRangeBrowser.Configure(areas, config, requireExplicitRanges: !known);
        var description = configured
            ? "目录来自配置范围，位索引连续递增并转换为字号.十六进制位号；未在线发现变量，权限及用途以现场为准。"
            : $"目录来自 {cpu} 常规寄存器范围，未在线发现变量或验证现场权限；T/C 分配须显式配置，变量名称及用途由工程确定。";
        return new PlcAddressRangeBrowser(selected, configured ? "ConfiguredRange" : "ModelRange", description);
    }

    private static PlcAddressArea Word(string id, string label, int start, int end)
        => new(id, label, start, end, DataType.UInt16, index => id + index.ToString(CultureInfo.InvariantCulture));

    private static PlcAddressArea Bits(string id, string label, int end, bool writable = true)
        => new(id, label, 0, end, DataType.Bool,
            index => $"{id}{(index / 16).ToString(CultureInfo.InvariantCulture)}.{(index % 16).ToString("X", CultureInfo.InvariantCulture)}", writable);

    private static string? Get(DeviceConnectionConfig config, string key)
        => config.ExtendedProperties.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
}
