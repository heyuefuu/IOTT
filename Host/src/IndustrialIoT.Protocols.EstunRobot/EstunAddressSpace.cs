namespace IndustrialIoT.Protocols.EstunRobot;

using System.Reflection;
using HslCommunication.Robot.Estun;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

/// <summary>
/// 埃斯顿机器人地址空间元数据（静态，全型号一致）。
///
/// 与华中机器人（<c>HuazhongRobotAddressSpace</c>，映射需集成商配置）不同，埃斯顿的寄存器布局
/// 由 HslCommunication <see cref="EstunTcpNet.ReadRobotData"/> 内部封装 —— 本文件只描述该快照
/// 暴露出的字段，不自行硬编码任何 Modbus 寄存器号，因此无"编造映射"风险。
///
/// 前提：机器人控制器需已启用 Modbus/TCP 数据交换区（SimDI / SimDout / 用户 AI / 用户 AO）。
/// 若现场固件的交换区布局与 Hsl 的假设不符，请改用原始 Modbus 地址直传（见 <see cref="EstunRobotDriver"/>）。
/// </summary>
internal static class EstunAddressSpace
{
    /// <summary>整机快照地址（返回 EstunData 的 JSON 序列化结果）。</summary>
    public const string SnapshotAddress = "ESTUN_DATA";

    /// <summary><c>DATA:</c> 前缀 —— 按属性名读取快照单字段。</summary>
    public const string DataPrefix = "DATA:";

    /// <summary><c>CMD:</c> 前缀 —— 只写，写入即触发对应机器人指令。</summary>
    public const string CommandPrefix = "CMD:";

    // 以下长度由 HslCommunication EstunData 的数组字段定义（SimDI/SimDout 各 64 bit，用户 AI/AO 各 32 个）
    public const int DiCount = 64;
    public const int DoCount = 64;
    public const int AiCount = 32;
    public const int AoCount = 32;

    /// <summary>快照字段描述。<paramref name="Property"/> 在静态构造时绑定，Hsl 改名会立即抛出而非静默返回空值。</summary>
    public sealed record Field(PropertyInfo Property, DataType DataType, bool IsWritable, string DisplayName);

    /// <summary>写入 <c>CMD:</c> 或可写 <c>DATA:</c> 地址时映射到的机器人指令。</summary>
    public enum Command
    {
        Start, Stop, ResetError, LoadProject, UnregisterProject, CommandStatusRestart, SetGlobalSpeed,
    }

    public static readonly IReadOnlyDictionary<string, Command> Commands =
        new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase)
        {
            ["Start"] = Command.Start,
            ["Stop"] = Command.Stop,
            ["ResetError"] = Command.ResetError,
            ["LoadProject"] = Command.LoadProject,
            ["UnregisterProject"] = Command.UnregisterProject,
            ["CommandStatusRestart"] = Command.CommandStatusRestart,
        };

    public static readonly IReadOnlyDictionary<string, string> CommandDescriptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Start"] = "启动程序 (RobotStartPrograme)",
            ["Stop"] = "停止程序 (RobotStopPrograme)",
            ["ResetError"] = "错误复位 (RobotResetError)",
            ["LoadProject"] = "装载工程，值为工程名 (RobotLoadProject)",
            ["UnregisterProject"] = "卸载工程 (RobotUnregisterProject)",
            ["CommandStatusRestart"] = "重置命令状态 (RobotCommandStatusRestart)",
        };

    /// <summary>可写快照字段 → 对应的机器人指令。</summary>
    public static readonly IReadOnlyDictionary<string, Command> WritableFields =
        new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase)
        {
            ["GlobalSpeedValue"] = Command.SetGlobalSpeed,
            ["ProjectName"] = Command.LoadProject,
        };

    public static readonly IReadOnlyDictionary<string, Field> Fields = BuildFields();

    private static Dictionary<string, Field> BuildFields()
    {
        // (属性名, 数据类型, 中文名) —— 数据类型对齐 EstunData 的实际 CLR 类型；数组字段以 JSON 字符串返回。
        (string Name, DataType Type, string Display)[] spec =
        [
            ("ErrorStatus",        DataType.Bool,   "错误状态"),
            ("EnableStatus",       DataType.Bool,   "使能状态"),
            ("RunStatus",          DataType.Bool,   "运行状态"),
            ("ProgramRunStatus",   DataType.Bool,   "程序运行状态"),
            ("RobotMoving",        DataType.Bool,   "机器人正在动作"),
            ("ManualMode",         DataType.Bool,   "手动模式"),
            ("AutoMode",           DataType.Bool,   "自动模式"),
            ("RemoteMode",         DataType.Bool,   "远程模式"),
            ("GlobalSpeedValue",   DataType.Int16,  "全局速度值"),
            ("ReadWriteFlag",      DataType.Int16,  "读写标志位"),
            ("RobotCommandStatus", DataType.UInt16, "机器人执行命令状态"),
            ("ProjectName",        DataType.String, "当前加载的工程名"),
            ("DI",                 DataType.String, "SimDI 全部 64 位 (JSON)"),
            ("DO",                 DataType.String, "SimDout 全部 64 位 (JSON)"),
            ("AI",                 DataType.String, "用户 AI 全部 32 个 (JSON)"),
            ("AO",                 DataType.String, "用户 AO 全部 32 个 (JSON)"),
        ];

        var map = new Dictionary<string, Field>(spec.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, type, display) in spec)
        {
            var prop = typeof(EstunData).GetProperty(name)
                ?? throw new InvalidOperationException(
                    $"HslCommunication EstunData 缺少属性 '{name}' —— 升级 HslCommunication 后需同步本映射表");
            map[name] = new Field(prop, type, WritableFields.ContainsKey(name), display);
        }
        return map;
    }

    /// <summary>构造浏览树的子节点。<paramref name="parentPath"/> 为空时返回根目录。</summary>
    public static IReadOnlyList<AddressNode> Browse(string? parentPath)
    {
        if (string.IsNullOrEmpty(parentPath))
            return
            [
                Folder("DATA", "状态数据 (EstunData 快照字段)"),
                Folder("IO", "数字/模拟 IO (快照)"),
                Folder("CMD", "机器人指令 (只写)"),
                Variable(SnapshotAddress, "整机状态快照 (JSON)", DataType.String, writable: false),
            ];

        if (parentPath.Equals("DATA", StringComparison.OrdinalIgnoreCase))
            return Fields.Select(kv =>
                Variable($"{DataPrefix}{kv.Key}", kv.Value.DisplayName, kv.Value.DataType, kv.Value.IsWritable)).ToList();

        if (parentPath.Equals("CMD", StringComparison.OrdinalIgnoreCase))
            return CommandDescriptions.Select(kv =>
                new AddressNode
                {
                    Path = $"{CommandPrefix}{kv.Key}",
                    DisplayName = kv.Value,
                    NodeType = AddressNodeType.Variable,
                    DataType = kv.Key.Equals("LoadProject", StringComparison.OrdinalIgnoreCase)
                        ? DataType.String : DataType.Bool,
                    IsReadable = false,
                    IsWritable = true,
                }).ToList();

        if (parentPath.Equals("IO", StringComparison.OrdinalIgnoreCase))
            return
            [
                Folder("IO/DI", $"SimDI 输入位 ×{DiCount}"),
                Folder("IO/DO", $"SimDout 输出位 ×{DoCount}"),
                Folder("IO/AI", $"用户模拟输入 ×{AiCount}"),
                Folder("IO/AO", $"用户模拟输出 ×{AoCount}"),
            ];

        if (parentPath.StartsWith("IO/", StringComparison.OrdinalIgnoreCase))
        {
            var area = parentPath[3..].ToUpperInvariant();
            if (!TryGetIoArea(area, out var count, out var dt)) return [];
            // 快照为只读投影：写 IO 需走原始 Modbus 线圈/寄存器地址直传
            return Enumerable.Range(0, count)
                .Select(i => Variable($"{area}{i}", $"{area}{i}", dt, writable: false))
                .ToList();
        }

        return [];
    }

    public static bool TryGetIoArea(string area, out int count, out DataType dataType)
    {
        (count, dataType) = area.ToUpperInvariant() switch
        {
            "DI" => (DiCount, DataType.Bool),
            "DO" => (DoCount, DataType.Bool),
            "AI" => (AiCount, DataType.Float),
            "AO" => (AoCount, DataType.Float),
            _ => (0, DataType.Bool),
        };
        return count > 0;
    }

    /// <summary>枚举全部内置地址，供 CSV / JSON 导出使用。</summary>
    public static IEnumerable<(string Path, string DisplayName, DataType DataType, bool Readable, bool Writable)> Enumerate()
    {
        yield return (SnapshotAddress, "整机状态快照", DataType.String, true, false);

        foreach (var (name, f) in Fields)
            yield return ($"{DataPrefix}{name}", f.DisplayName, f.DataType, true, f.IsWritable);

        foreach (var area in new[] { "DI", "DO", "AI", "AO" })
        {
            TryGetIoArea(area, out var count, out var dt);
            for (int i = 0; i < count; i++)
                yield return ($"{area}{i}", $"{area}{i}", dt, true, false);
        }

        foreach (var (name, desc) in CommandDescriptions)
            yield return ($"{CommandPrefix}{name}", desc,
                name.Equals("LoadProject", StringComparison.OrdinalIgnoreCase) ? DataType.String : DataType.Bool,
                false, true);
    }

    private static AddressNode Folder(string path, string name) => new()
    {
        Path = path, DisplayName = name, NodeType = AddressNodeType.Folder,
        IsReadable = false, IsWritable = false,
    };

    private static AddressNode Variable(string path, string name, DataType dt, bool writable) => new()
    {
        Path = path, DisplayName = name, NodeType = AddressNodeType.Variable,
        DataType = dt, IsReadable = true, IsWritable = writable,
    };
}
