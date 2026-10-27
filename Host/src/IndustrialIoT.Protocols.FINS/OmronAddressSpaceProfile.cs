namespace IndustrialIoT.Protocols.FINS;

using System.Globalization;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Addressing;

public static class OmronAddressSpaceProfile
{
    public static IAddressSpaceBrowser Create(DeviceConnectionConfig config, string? model = null)
    {
        var cpu = (string.IsNullOrWhiteSpace(model) ? Get(config, "Model") : model)?.Trim().ToUpperInvariant() ?? "";
        var cj2m = cpu == "CJ2M" || cpu.StartsWith("CJ2M-CPU", StringComparison.Ordinal);
        var cp1h = cpu == "CP1H" || cpu.StartsWith("CP1H-", StringComparison.Ordinal);
        var cp1en = cpu.StartsWith("CP1E-N", StringComparison.Ordinal);
        var cp1ee = cpu.StartsWith("CP1E-E", StringComparison.Ordinal);
        var known = cj2m || cp1h || cp1en || cp1ee;
        var smallCpu = cp1en || cp1ee;
        var configured = !string.IsNullOrWhiteSpace(Get(config, "AddressSpaceRanges"));
        var areas = new List<PlcAddressArea>
        {
            Word("DM", "数据存储区", 0, cp1ee ? 2047 : cp1en ? 8191 : known ? 32767 : 65535),
            Word("CIO", "输入输出及内部工作区", 0, smallCpu ? 289 : known ? 6143 : 65535),
            Word("WR", "工作区", 0, smallCpu ? 99 : known ? 511 : 65535),
            Word("HR", "保持区", 0, smallCpu ? 49 : known ? 511 : 65535),
            Word("AR", "辅助区（只读）", 0, 447, writable: false),
            Word("AR_RW", "辅助区（可写）", 448, smallCpu ? 753 : cj2m ? 1471 : known ? 959 : 65535, "AR"),
        };
        if (cj2m) areas.Add(Word("AR_EXT", "扩展辅助区（只读）", 10000, 11535, "AR", false));
        var selected = PlcAddressRangeBrowser.Configure(areas, config, requireExplicitRanges: !known);
        var description = configured
            ? "目录来自配置的寄存器范围，未在线发现变量；可读写性及实际用途以现场 PLC 为准。"
            : $"目录来自 {cpu} 型号的寄存器范围，未在线发现变量；未验证现场权限或程序是否使用这些地址。";
        return new PlcAddressRangeBrowser(selected, configured ? "ConfiguredRange" : "ModelRange", description);
    }

    private static PlcAddressArea Word(string id, string label, int start, int end, string? prefix = null, bool writable = true)
        => new(id, label, start, end, DataType.UInt16,
            index => (prefix ?? id) + index.ToString(CultureInfo.InvariantCulture), writable);

    private static string? Get(DeviceConnectionConfig config, string key)
        => config.ExtendedProperties.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
}
