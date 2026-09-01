namespace IndustrialIoT.Protocols.NCLinkApi;

/// <summary>
/// NC-Link API Server 数据字典常量 — 对照《NC-Link应用开发指导手册》第 8 章附录一。
/// 全部路径直接用于 HTTP 请求体的 path 字段。
/// </summary>
public static class NCLinkApiPaths
{
    public const string MachineStatus = "/MACHINE/STATUS";
    public const string FeedSpeed = "/MACHINE/FEED_SPEED";
    public const string FeedOverride = "/MACHINE/FEED_OVERRIDE";
    public const string SpindleOverride = "/MACHINE/SPDL_OVERRIDE";
    public const string SpindleOverrideV241 = "/MACHINE/SPINDLE_OVERRIDE";
    public const string PartCount = "/MACHINE/PART_COUNT";
    public const string ProgramNumber = "/MACHINE/PROGRAM_NUMBER";

    public const string Controller = "/MACHINE/CONTROLLER";
    public const string Program = "/MACHINE/CONTROLLER/PROGRAM";
    public const string Warning = "/MACHINE/CONTROLLER/WARNING";
    public const string Parameter = "/MACHINE/CONTROLLER/PARAMETER";
    public const string ToolParam = "/MACHINE/CONTROLLER/TOOL_PARAM";
    public const string Coordinate = "/MACHINE/CONTROLLER/COORDINATE";
    public const string Console = "/MACHINE/CONTROLLER/CONSOLE";
    public const string File = "/MACHINE/CONTROLLER/FILE";

    /// <summary>寄存器 X — 手册 8.1.2，按 index 取下标。</summary>
    public const string VariableRegX = "/MACHINE/CONTROLLER/VARIABLE@REG_X";

    /// <summary>寄存器 Y。</summary>
    public const string VariableRegY = "/MACHINE/CONTROLLER/VARIABLE@REG_Y";

    /// <summary>寄存器 R。</summary>
    public const string VariableRegR = "/MACHINE/CONTROLLER/VARIABLE@REG_R";

    /// <summary>寄存器 F。</summary>
    public const string VariableRegF = "/MACHINE/CONTROLLER/VARIABLE@REG_F";

    /// <summary>寄存器 G — 采样使能 G2960.12 在此（手册 8.1.22 步骤 4）。</summary>
    public const string VariableRegG = "/MACHINE/CONTROLLER/VARIABLE@REG_G";

    /// <summary>寄存器 B。</summary>
    public const string VariableRegB = "/MACHINE/CONTROLLER/VARIABLE@REG_B";

    /// <summary>系统数据 — 手册 8.1.3 / 表 8-1，40 项。</summary>
    public const string VariableSys = "/MACHINE/CONTROLLER/VARIABLE@SYS";

    /// <summary>通道 0 数据 — 手册 8.1.4 / 表 8-2，60 项；通道号 0/1/2…</summary>
    public const string VariableChan0 = "/MACHINE/CONTROLLER/VARIABLE@CHAN_0";

    /// <summary>轴 0 数据 — 手册 8.1.5 / 表 8-3，57 项；轴号 0~5 对应 X/Y/Z/A/B/C。</summary>
    public const string VariableAxis0 = "/MACHINE/CONTROLLER/VARIABLE@AXIS_0";

    /// <summary>宏变量 — 手册 8.1.7。</summary>
    public const string VariableMacro = "/MACHINE/CONTROLLER/VARIABLE@MACRO";

    /// <summary>采样客户端 — 手册 8.1.22 二步、三步、四步均用此 path。</summary>
    public const string VariableSysSmpl = "/MACHINE/CONTROLLER/VARIABLE@SYS_SMPL";

    /// <summary>动态构造通道路径，channelIndex 0..N。</summary>
    public static string VariableChan(int channelIndex) =>
        $"/MACHINE/CONTROLLER/VARIABLE@CHAN_{channelIndex}";

    /// <summary>动态构造轴路径，axisIndex 0..5（X/Y/Z/A/B/C）。</summary>
    public static string VariableAxis(int axisIndex) =>
        $"/MACHINE/CONTROLLER/VARIABLE@AXIS_{axisIndex}";
}

/// <summary>
/// 手册表 5-1 定义的 operation 枚举字符串常量。
/// </summary>
public static class NCLinkApiOperations
{
    public const string GetValue = "get_value";
    public const string SetValue = "set_value";
    public const string GetLength = "get_length";
    public const string GetKeys = "get_keys";
    public const string GetAttributes = "get_attributes";
    public const string Add = "add";
    public const string Delete = "delete";
}
