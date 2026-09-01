namespace IndustrialIoT.Protocols.Modbus;

using System.Text.RegularExpressions;
using IndustrialIoT.Domain.Enums;

public static class ModbusAddressParser
{
    /// <param name="RegisterCount">
    /// 寄存器区：要读的 16 位寄存器个数。位区（线圈 / 离散输入）：要读的位个数。
    /// </param>
    /// <param name="CountExplicit">数量来自地址里的 <c>;N</c> 后缀，而非由 DataType 推导。</param>
    public record ParsedAddress(
        ModbusRegisterType RegisterType, int StartAddress, int RegisterCount, bool CountExplicit = false);

    public enum ModbusRegisterType { HoldingRegister, InputRegister, Coil, DiscreteInput }

    /// <summary>单次请求可读的最大寄存器数（Modbus FC03/04 的报文上限是 125）。</summary>
    public const int MaxRegistersPerRequest = 125;

    /// <summary>单次请求可读的最大位数（Modbus FC01/02 的报文上限是 2000）。</summary>
    public const int MaxBitsPerRequest = 2000;

    private static readonly (string Prefix, ModbusRegisterType Type)[] PrefixMap =
    [
        ("HR", ModbusRegisterType.HoldingRegister),
        ("IR", ModbusRegisterType.InputRegister),
        ("DI", ModbusRegisterType.DiscreteInput),
        ("C",  ModbusRegisterType.Coil),
    ];

    /// <summary>Modicon 区号前缀（4x / 3x / 1x / 0x），其后为 0 基协议地址。</summary>
    private static readonly (string Prefix, ModbusRegisterType Type)[] AreaPrefixMap =
    [
        ("4X", ModbusRegisterType.HoldingRegister),
        ("3X", ModbusRegisterType.InputRegister),
        ("1X", ModbusRegisterType.DiscreteInput),
        ("0X", ModbusRegisterType.Coil),
    ];

    /// <summary>正好 5 或 6 位十进制的 Modicon 引用号，如 40001 / 400001。</summary>
    private static readonly Regex ReferenceRegex = new(@"^\d{5}$|^\d{6}$", RegexOptions.Compiled);

    private static readonly Regex BareNumberRegex = new(@"^\d+$", RegexOptions.Compiled);

    /// <summary>
    /// 解析 Modbus 地址。三种写法都支持，同一个寄存器可以任选其一：
    ///
    /// <list type="table">
    /// <listheader><term>写法</term><description>含义</description></listheader>
    /// <item>
    ///   <term>HR0 / IR0 / C0 / DI0</term>
    ///   <description>区域前缀 + <b>0 基协议地址</b>（报文里实际发送的地址）。</description>
    /// </item>
    /// <item>
    ///   <term>4x0 / 3x0 / 1x0 / 0x40</term>
    ///   <description>Modicon 区号 + <b>0 基协议地址</b>，与 HslCommunication 的写法一致。
    ///   注意 <c>0x40</c> 里的 40 按<b>十进制</b>解析（线圈 40），不是十六进制。</description>
    /// </item>
    /// <item>
    ///   <term>40001 / 30001 / 10001 / 00001</term>
    ///   <description>5 位（或 6 位，如 400001）Modicon <b>引用号</b>，序号从 1 开始，
    ///   减去区基地址后即协议地址。例：40001→HR0、40100→HR99、10001→DI0。</description>
    /// </item>
    /// <item>
    ///   <term>36</term>
    ///   <description>无前缀的裸数字按<b>保持寄存器的 0 基协议地址</b>处理（等价 HR36），与 Hsl 一致。</description>
    /// </item>
    /// <item>
    ///   <term>任意写法 + <c>;N</c></term>
    ///   <description>显式指定数量，覆盖由 DataType 推导的长度。寄存器区是 N 个 16 位寄存器，
    ///   位区是 N 个位。例：<c>40001;100</c> 读 100 个保持寄存器、<c>DI0;64</c> 读 64 个离散输入、
    ///   <c>40053;10</c> 配 String 读 20 字节字符串。</description>
    /// </item>
    /// </list>
    ///
    /// 歧义处理：<b>恰好 5 位或 6 位</b>且首位是 0/1/3/4 的数字一律当引用号。若确实要读协议地址
    /// 40001 这样的大地址，请写成 <c>HR40001</c> 或 <c>4x40001</c>，不要写裸的 5 位数。
    /// </summary>
    public static ParsedAddress Parse(string address, DataType dataType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var trimmed = address.Trim().ToUpperInvariant();

        // ";N" 数量后缀：显式覆盖由 DataType 推导的长度
        int? explicitCount = null;
        var semicolon = trimmed.IndexOf(';');
        if (semicolon >= 0)
        {
            var countPart = trimmed[(semicolon + 1)..].Trim();
            if (!int.TryParse(countPart, out var count) || count < 1)
                throw new FormatException(
                    $"'{address}' 的数量后缀无效。';' 后面必须是不小于 1 的整数，如 \"40001;100\"。");

            explicitCount = count;
            trimmed = trimmed[..semicolon].Trim();

            if (trimmed.Length == 0)
                throw new FormatException($"'{address}' 缺少地址部分，';' 前面要写地址，如 \"40001;100\"。");
        }

        var parsed = ParseBase(trimmed, address, dataType);
        if (explicitCount is null) return parsed;

        var limit = IsBitType(parsed.RegisterType) ? MaxBitsPerRequest : MaxRegistersPerRequest;
        if (explicitCount > limit)
            throw new FormatException(
                $"'{address}' 请求 {explicitCount} 个{(IsBitType(parsed.RegisterType) ? "位" : "寄存器")}，" +
                $"超过 Modbus 单次请求上限 {limit}。请拆成多次读取。");

        return parsed with { RegisterCount = explicitCount.Value, CountExplicit = true };
    }

    private static ParsedAddress ParseBase(string trimmed, string original, DataType dataType)
    {
        // 1. 区域前缀 + 0 基协议地址：HR0 / IR0 / C0 / DI0
        foreach (var (prefix, regType) in PrefixMap)
        {
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var numPart = trimmed[prefix.Length..];
            if (!int.TryParse(numPart, out var startAddress) || startAddress < 0)
                throw new FormatException($"Invalid address number in '{original}'. Expected non-negative integer after prefix '{prefix}'.");

            return new ParsedAddress(regType, startAddress, GetRegisterCount(regType, dataType));
        }

        // 2. Modicon 区号 + 0 基协议地址：4x0 / 3x0 / 1x0 / 0x40（数字按十进制）
        foreach (var (prefix, regType) in AreaPrefixMap)
        {
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var numPart = trimmed[prefix.Length..];
            if (!int.TryParse(numPart, out var startAddress) || startAddress < 0)
                throw new FormatException(
                    $"Invalid address number in '{original}'. Expected a non-negative decimal address after '{prefix}' " +
                    $"(e.g. \"{prefix.ToLowerInvariant()}100\").");

            return new ParsedAddress(regType, startAddress, GetRegisterCount(regType, dataType));
        }

        // 3. 5/6 位 Modicon 引用号（1 基）：40001 / 30001 / 10001 / 00001
        // 一旦形如引用号就必须按引用号解释成功，不能悄悄退化成协议地址 ——
        // 否则手册基准写成 40000 的用户会拿到 HR40000 这种看似成功、实则读错位置的结果。
        if (ReferenceRegex.IsMatch(trimmed) && IsReferenceArea(trimmed[0]))
        {
            if (TryParseReference(trimmed, out var refType, out var refAddress))
                return new ParsedAddress(refType, refAddress, GetRegisterCount(refType, dataType));

            throw new FormatException(
                $"'{original}' 形如 Modicon 引用号，但小于所在区的基准值（引用号从 1 开始：" +
                "00001 是第一个线圈、10001 第一个离散输入、30001 第一个输入寄存器、40001 第一个保持寄存器）。" +
                "若你要表达的是 0 基协议地址，请改用 HR/IR/C/DI 或 4x/3x/1x/0x 前缀，如 \"HR0\"、\"4x0\"。");
        }

        // 4. 裸数字 → 保持寄存器 0 基协议地址（与 Hsl 的 Read("36", ...) 一致）
        if (BareNumberRegex.IsMatch(trimmed) && int.TryParse(trimmed, out var bare) && bare >= 0)
            return new ParsedAddress(
                ModbusRegisterType.HoldingRegister, bare,
                GetRegisterCount(ModbusRegisterType.HoldingRegister, dataType));

        throw new FormatException(
            $"Unrecognized Modbus address '{original}'. Supported forms: " +
            "HR100 / IR100 / C0 / DI0（0 基协议地址）、4x100 / 3x100 / 1x0 / 0x40（0 基协议地址）、" +
            "40101 / 30001 / 10001 / 00001（5 位 Modicon 引用号，1 基）、100（裸数字＝保持寄存器 0 基地址）、" +
            "任意写法加 \";N\" 指定数量（如 \"40001;100\"）。");
    }

    /// <summary>首位数字是否落在 Modicon 已定义的四个区（0/1/3/4）。2xxxx 段未定义。</summary>
    private static bool IsReferenceArea(char leading) => leading is '0' or '1' or '3' or '4';

    /// <summary>
    /// 把 5 位或 6 位 Modicon 引用号换算成区域 + 0 基协议地址。
    /// 引用号序号从 1 开始，故 40001（或 400001）→ 地址 0。首位为 2 的段未定义，直接拒绝。
    /// </summary>
    private static bool TryParseReference(string digits, out ModbusRegisterType type, out int address)
    {
        type = default;
        address = 0;

        if (!int.TryParse(digits, out var value)) return false;

        // 5 位：40001 起；6 位：400001 起
        var span = digits.Length == 6 ? 100000 : 10000;

        (int Basis, ModbusRegisterType Type)? area = digits[0] switch
        {
            '0' => (1, ModbusRegisterType.Coil),                    // 00001.. / 000001..
            '1' => (span + 1, ModbusRegisterType.DiscreteInput),    // 10001.. / 100001..
            '3' => (span * 3 + 1, ModbusRegisterType.InputRegister),
            '4' => (span * 4 + 1, ModbusRegisterType.HoldingRegister),
            _ => null,
        };

        if (area is null) return false;

        var offset = value - area.Value.Basis;
        if (offset < 0) return false;   // 例如 00000：引用号从 1 起，没有第 0 个

        type = area.Value.Type;
        address = offset;
        return true;
    }

    /// <summary>
    /// Returns the number of 16-bit registers needed to represent the given <paramref name="dataType"/>.
    /// For bit-level register types (Coil / DiscreteInput), always returns 1.
    /// </summary>
    public static int GetRegisterCount(ModbusRegisterType registerType, DataType dataType)
    {
        // Coils and discrete inputs are single-bit; always 1 point
        if (registerType is ModbusRegisterType.Coil or ModbusRegisterType.DiscreteInput)
            return 1;

        return dataType switch
        {
            DataType.Bool    => 1,
            DataType.Int16   => 1,
            DataType.UInt16  => 1,
            DataType.Int32   => 2,
            DataType.UInt32  => 2,
            DataType.Float   => 2,
            DataType.Int64   => 4,
            DataType.Double  => 4,
            DataType.String  => 1,     // caller should override for multi-register strings
            DataType.ByteArray => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "Unsupported DataType for Modbus register count calculation.")
        };
    }

    /// <summary>
    /// Returns true when the register type represents a single-bit address space.
    /// </summary>
    public static bool IsBitType(ModbusRegisterType registerType) =>
        registerType is ModbusRegisterType.Coil or ModbusRegisterType.DiscreteInput;
}
