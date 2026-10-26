namespace IndustrialIoT.Protocols.NCLink.BrandAdapters;

/// <summary>
/// NC-Link brand adapter — 双向映射: 标准路径 ↔ NC-Link DataItem ID。
/// 标准路径供上层统一使用 (如 /CNC/Spindle/Speed)，
/// DataItem ID 是 NC-Link 协议中实际使用的标识 (如 010302)。
/// </summary>
public interface IBrandAdapter
{
    string Brand { get; }
    /// <summary>标准路径 → DataItem ID（发给设备）。</summary>
    string MapPath(string standardPath);
    /// <summary>DataItem ID → 标准路径（返回给上层）。</summary>
    string MapPathReverse(string devicePath);
}

/// <summary>
/// 透传适配器 — 地址即 ID，直接使用。
/// 适用于完全遵循 NC-Link 标准的系统，或上层直接传 DataItem ID 的场景。
/// </summary>
public sealed class DefaultBrandAdapter : IBrandAdapter
{
    public string Brand => "Default";
    public string MapPath(string standardPath) => standardPath;
    public string MapPathReverse(string devicePath) => devicePath;
}

/// <summary>
/// 华中数控 (HNC) — 基于甲方 model.json 中的 DataItem ID 定义。
/// 支持 HNC-808/818/848 系列。
/// </summary>
public sealed class HNCBrandAdapter : IBrandAdapter
{
    public string Brand => "华中数控";

    private static readonly Dictionary<string, string> _pathToId = new(StringComparer.OrdinalIgnoreCase)
    {
        // 主轴（HNC 848 模型：010303=进给速度，010306=主轴倍率；模型未单独提供主轴转速/负载）
        ["/CNC/Spindle/Speed"] = "010303",
        ["/CNC/Spindle/Load"] = "010306",

        // 程序（01035409=主程序名，01035412=报警，010302=机床状态）
        ["/CNC/Program/Number"] = "01035409",
        ["/CNC/Program/Status"] = "01035412",

        // 系统状态
        ["/CNC/Mode"] = "010302",
        ["/CNC/PartCount"] = "010307",

        // 报警（01035412=报警）
        ["/CNC/Alarm/Active"] = "01035412",
        ["/CNC/Alarm/Code"] = "01035412",
        ["/CNC/Alarm/Message"] = "01035412",

        // 轴 — X（01035022 丝杠：01=实际位置；01035020 驱动：03=指令速度）
        ["/CNC/Axis/X/Position"] = "0103502201",
        ["/CNC/Axis/X/FeedRate"] = "0103502003",
        // 轴 — Y
        ["/CNC/Axis/Y/Position"] = "0103512201",
        ["/CNC/Axis/Y/FeedRate"] = "0103512003",
        // 轴 — Z
        ["/CNC/Axis/Z/Position"] = "0103522201",
        ["/CNC/Axis/Z/FeedRate"] = "0103522003",
        // 轴 — A/B（模型以 AXIS_3/4/5 列表暴露）
        ["/CNC/Axis/A/Position"] = "01035458",
        ["/CNC/Axis/A/FeedRate"] = "01035459",
        ["/CNC/Axis/B/Position"] = "01035460",
        ["/CNC/Axis/B/FeedRate"] = "01035458",
        // 轴 — C（010353 轴：01=实际位置，02=实际速度）
        ["/CNC/Axis/C/Position"] = "0103532101",
        ["/CNC/Axis/C/FeedRate"] = "0103532002",

        // 进给
        ["/CNC/FeedRate"] = "010303",
        ["/CNC/FeedOverride"] = "010305",
        ["/CNC/SpindleOverride"] = "010306",
    };

    private static readonly Dictionary<string, string> _idToPath = BuildReverseMap();

    private static Dictionary<string, string> BuildReverseMap()
    {
        // 多个标准路径可能映射到同一个 DataItem（如报警三字段都指向 01035412），
        // 反向映射保留第一个，避免 ToDictionary 因重复键在静态构造时抛异常。
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, id) in _pathToId) map.TryAdd(id, path);
        return map;
    }

    public string MapPath(string standardPath)
        => _pathToId.TryGetValue(standardPath, out var id) ? id : standardPath;

    public string MapPathReverse(string devicePath)
        => _idToPath.TryGetValue(devicePath, out var path) ? path : devicePath;
}

/// <summary>
/// 广州数控 (GSK) — GSK 980/988 系列。
/// 与 HNC 类似的 ID 方案但有差异。
/// </summary>
public sealed class GSKBrandAdapter : IBrandAdapter
{
    public string Brand => "广州数控";

    private static readonly Dictionary<string, string> _pathToId = new(StringComparer.OrdinalIgnoreCase)
    {
        // 主轴
        ["/CNC/Spindle/Speed"] = "020102",
        ["/CNC/Spindle/Load"] = "020103",

        // 程序
        ["/CNC/Program/Number"] = "020201",
        ["/CNC/Program/Status"] = "020202",

        // 系统状态
        ["/CNC/Mode"] = "020301",
        ["/CNC/PartCount"] = "020302",

        // 报警
        ["/CNC/Alarm/Active"] = "020401",
        ["/CNC/Alarm/Code"] = "020402",
        ["/CNC/Alarm/Message"] = "020403",

        // 轴
        ["/CNC/Axis/X/Position"] = "020501",
        ["/CNC/Axis/X/FeedRate"] = "020502",
        ["/CNC/Axis/Y/Position"] = "020511",
        ["/CNC/Axis/Y/FeedRate"] = "020512",
        ["/CNC/Axis/Z/Position"] = "020521",
        ["/CNC/Axis/Z/FeedRate"] = "020522",
    };

    private static readonly Dictionary<string, string> _idToPath =
        _pathToId.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    public string MapPath(string standardPath)
        => _pathToId.TryGetValue(standardPath, out var id) ? id : standardPath;

    public string MapPathReverse(string devicePath)
        => _idToPath.TryGetValue(devicePath, out var path) ? path : devicePath;
}

// 北京精雕（JingDiao）不走 NC-Link：其官方联网协议是私有 RPC over TCP，由 SDK
// NcMonIO.dll / NcMonIOEx.ocx 封装（端口 RPC=89、回调=7080、上传=7081、下载=7082；32 位）。
// 原 JingDiaoBrandAdapter 的点位 ID（JD_SPD_SPEED…）是占位符、非真实 DataItem，已移除以免误标"已支持"。
// 正确实现：仿 NativeFocasApi(P/Invoke Fwlib64.dll) 或 GskShim/HncSdkShim(32 位 native + IPC) 做 NcMonIO.dll 封装。
// SDK 与官方说明：Services\北京精雕50系统联网监控开发包及说明V20161219\

public static class BrandAdapterFactory
{
    private static readonly Dictionary<string, Func<IBrandAdapter>> _adapters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["华中数控"] = () => new HNCBrandAdapter(),
        ["HNC"] = () => new HNCBrandAdapter(),
        ["HNC-808"] = () => new HNCBrandAdapter(),
        ["HNC-818"] = () => new HNCBrandAdapter(),
        ["HNC-848"] = () => new HNCBrandAdapter(),
        ["HNC-848Di"] = () => new HNCBrandAdapter(),
    };

    public static IBrandAdapter Create(string? brand)
    {
        if (brand is not null && _adapters.TryGetValue(brand, out var factory))
            return factory();
        return new DefaultBrandAdapter();
    }
}
