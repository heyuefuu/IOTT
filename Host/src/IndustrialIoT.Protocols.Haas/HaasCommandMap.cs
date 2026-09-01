namespace IndustrialIoT.Protocols.Haas;

using System.Globalization;
using System.Text.RegularExpressions;
using IndustrialIoT.Domain.Enums;

/// <summary>
/// Haas MDC 地址映射与响应解析。
/// 支持两类地址：
///   1. 命名地址（预定义）→ Q 命令：Mode/PartCount/SerialNumber/...
///   2. 宏变量地址 "Macro:xxxxx" → Q600 读取 / Exxxxx 写入
/// 响应格式：">Qxxx VALUE" 或 ">Q500 STATUS, PROGRAM O12345, PARTS 123"
/// 宏变量响应："MACRO, xxxxx, value"
/// </summary>
internal static class HaasCommandMap
{
    private const string MacroPrefix = "Macro:";

    private static readonly Dictionary<string, string> NameToQ = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SerialNumber"] = "Q100",
        ["SoftwareVersion"] = "Q101",
        ["Model"] = "Q102",
        ["Mode"] = "Q104",
        ["ToolChanges"] = "Q200",
        ["ToolNumber"] = "Q201",
        ["PowerOnTime"] = "Q300",
        ["RunTime"] = "Q301",
        ["CycleTime"] = "Q303",
        ["PreviousCycleTime"] = "Q304",
        ["PartCount"] = "Q402",
        ["PartCount2"] = "Q403",
        ["Status"] = "Q500",
        ["LastAlarm"] = "Q700",
    };

    /// <summary>命名地址 → 系统宏变量号；底层仍走 Q600 NNNNN。
    /// 编号取自 Haas 操作手册附录 G "Macro Variables" 与 Fanuc 兼容惯例。</summary>
    private static readonly Dictionary<string, int> NameToMacro = new(StringComparer.OrdinalIgnoreCase)
    {
        // 机械坐标（#5021-5026 = X/Y/Z/A/B/C）
        ["Position:X"] = 5021, ["Position:Y"] = 5022, ["Position:Z"] = 5023,
        ["Position:A"] = 5024, ["Position:B"] = 5025, ["Position:C"] = 5026,
        // 工件坐标（#5041-5046）
        ["WorkPosition:X"] = 5041, ["WorkPosition:Y"] = 5042, ["WorkPosition:Z"] = 5043,
        ["WorkPosition:A"] = 5044, ["WorkPosition:B"] = 5045, ["WorkPosition:C"] = 5046,
        // Haas 特有
        ["SpindleRPM"] = 3027,   // 当前主轴 RPM
        ["SpindleLoad"] = 1094,  // 主轴负载百分比（Haas NGC）
    };

    public static IReadOnlyList<string> NamedAddresses => NameToQ.Keys
        .Concat(NameToMacro.Keys)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>仅 Q 命令命名地址（不含基于系统宏的派生名）。</summary>
    public static IReadOnlyCollection<string> QNamedAddresses => NameToQ.Keys;

    /// <summary>系统宏派生的命名地址（按前缀分组：Position / WorkPosition / Spindle）。</summary>
    public static IReadOnlyCollection<string> SystemMacroNames => NameToMacro.Keys;

    /// <summary>地址 → 实际要发送的命令字符串（Q / Q600 xxxxx）。</summary>
    public static bool TryBuildReadCommand(string address, out string command)
    {
        if (TryParseMacro(address, out var macroNum))
        {
            command = $"Q600 {macroNum:D5}";
            return true;
        }
        if (NameToMacro.TryGetValue(address, out var systemMacroNum))
        {
            command = $"Q600 {systemMacroNum:D5}";
            return true;
        }
        if (NameToQ.TryGetValue(address, out var q))
        {
            command = q;
            return true;
        }
        // 未知地址：假设调用方已经传入原始 Q 命令（如 "Q104"）
        if (address.StartsWith('Q') && address.Length >= 2)
        {
            command = address;
            return true;
        }
        command = "";
        return false;
    }

    /// <summary>地址 → E 写入命令（仅宏变量支持写入；系统宏 #5021 等只读，不允许写）。</summary>
    public static bool TryBuildWriteCommand(string address, object value, out string command)
    {
        if (NameToMacro.ContainsKey(address))
        {
            command = "";
            return false; // 系统宏变量只读
        }
        if (TryParseMacro(address, out var macroNum))
        {
            var formatted = value switch
            {
                double d => d.ToString(CultureInfo.InvariantCulture),
                float f => f.ToString(CultureInfo.InvariantCulture),
                int i => i.ToString(CultureInfo.InvariantCulture),
                long l => l.ToString(CultureInfo.InvariantCulture),
                bool b => b ? "1" : "0",
                _ => value?.ToString() ?? "0",
            };
            command = $"E{macroNum:D5} {formatted}";
            return true;
        }
        command = "";
        return false;
    }

    private static bool TryParseMacro(string address, out int macroNumber)
    {
        macroNumber = 0;
        if (!address.StartsWith(MacroPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(address.AsSpan(MacroPrefix.Length), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out macroNumber) && macroNumber is > 0 and < 100000;
    }

    /// <summary>解析 ">Q104 IDLE" 或 "MACRO, 10001, 42.0" 这类响应，提取关心的值字符串。</summary>
    public static string ExtractValue(string rawResponse, string commandSent)
    {
        if (string.IsNullOrWhiteSpace(rawResponse)) return "";
        var trimmed = rawResponse.Trim().TrimStart('>').Trim();

        // 宏变量响应："MACRO, 10001, value"
        var macroMatch = Regex.Match(trimmed, @"^MACRO,\s*\d+,\s*(?<val>.+?)\s*$",
            RegexOptions.IgnoreCase);
        if (macroMatch.Success) return macroMatch.Groups["val"].Value.Trim();

        // 普通 Q 响应："Q104 IDLE" 或 "Q500 IDLE, NO PROGRAM, 0"
        // 剥掉前缀 Q 命令码，余下就是值
        var qPrefix = Regex.Match(trimmed, @"^Q\d+\s*(?<rest>.*)$", RegexOptions.IgnoreCase);
        if (qPrefix.Success) return qPrefix.Groups["rest"].Value.Trim();

        return trimmed;
    }

    /// <summary>写入响应成功判定。Haas NGC 成功时回显命令，失败回 "?" 或错误文本。</summary>
    public static bool IsWriteSuccessful(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse)) return false;
        var trimmed = rawResponse.Trim();
        return !trimmed.Contains('?') &&
               !trimmed.Contains("INVALID", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
    }
}
