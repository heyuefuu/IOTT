namespace IndustrialIoT.Protocols.Inovance;

using System.Text.RegularExpressions;
using HslCommunication.Profinet.Inovance;
using IndustrialIoT.Domain.ValueObjects;

public static class InovanceAddressSpace
{
    private sealed record Area(Func<string, string?> Normalize);
    private static readonly Regex StationRegex = new(@"^(?:(?<station>s=\d+);)?(?<core>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Dictionary<InovanceSeries, Area[]> Areas = new()
    {
        [InovanceSeries.AM] =
        [
            BitArea("Q", "Q", "QX"),
            BitArea("IX", "IX", "I"),
            BitArea("MX", "MX"),
            ScalarArea("MW"),
            ScalarArea("MD"),
            ScalarArea("MB"),
            ScalarArea("SM"),
            ScalarArea("SD"),
        ],
        [InovanceSeries.H3U] =
        [
            ScalarArea("M"),
            ScalarArea("SM"),
            ScalarArea("S"),
            ScalarArea("T"),
            ScalarArea("C"),
            PointArea("X", octal: true),
            PointArea("Y", octal: true),
            ScalarArea("D"),
            ScalarArea("SD"),
            ScalarArea("R"),
        ],
        [InovanceSeries.H5U] =
        [
            ScalarArea("M"),
            ScalarArea("B"),
            ScalarArea("S"),
            PointArea("X", octal: true),
            PointArea("Y", octal: true),
            ScalarArea("D"),
            ScalarArea("R"),
        ],
        [InovanceSeries.Easy] =
        [
            ScalarArea("M"),
            ScalarArea("B"),
            ScalarArea("S"),
            PointArea("X", octal: true),
            PointArea("Y", octal: true),
            ScalarArea("D"),
            ScalarArea("R"),
        ],
    };

    public static InovanceSeries? ParseSeries(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToUpperInvariant() switch
    {
        "AM" or "AM400" or "AM400-800" or "AM400_800" or "AM600" or "AM800" or "AC" or "AP" => InovanceSeries.AM,
        "H3U" or "XP" => InovanceSeries.H3U,
        "H5U" => InovanceSeries.H5U,
        "EASY" => InovanceSeries.Easy,
        _ when Enum.TryParse<InovanceSeries>(raw, true, out var parsed) => parsed,
        _ => null,
    };

    /// <summary>Series 的可选值提示，用于连接失败时给出可操作的错误信息。</summary>
    public const string SupportedSeriesHint = "H3U（含 XP） / H5U / AM（含 AM400、AM600、AM800、AC、AP） / Easy";

    /// <summary>
    /// 解析 Series，并区分"未填"与"填了但认不出"两种情况 —— 两者的处置动作不同，
    /// 混用同一句报错会让现场以为漏填，实际是型号名写错。
    /// </summary>
    public static InovanceSeries ParseSeriesOrThrow(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException($"汇川协议必须提供 ExtendedProperties['Series']，可选值：{SupportedSeriesHint}");
        return ParseSeries(raw)
            ?? throw new InvalidOperationException($"无法识别的汇川系列 '{raw}'，可选值：{SupportedSeriesHint}");
    }

    public static string Normalize(string address, InovanceSeries series)
    {
        var match = StationRegex.Match(address.Trim());
        if (!match.Success) throw new FormatException($"Invalid Inovance address: '{address}'.");
        var station = match.Groups["station"].Success ? match.Groups["station"].Value.ToLowerInvariant() + ";" : string.Empty;
        var core = match.Groups["core"].Value.Trim().ToUpperInvariant();
        var normalized = GetAreas(series).Select(area => area.Normalize(core)).FirstOrDefault(value => value is not null)
            ?? throw new FormatException($"Address '{address}' is not valid for series '{series}'.");
        if (!IsSupportedByHsl(series, station + normalized)) throw new FormatException($"Address '{address}' is not valid for series '{series}'.");
        return station + normalized;
    }

    public static string GetPingAddress(InovanceSeries series) => series == InovanceSeries.AM ? "SD0" : "D0";

    /// <summary>
    /// 读取扩展属性。ExtendedProperties 是 JSON 反序列化出的普通字典（区分大小写），而前端早期版本
    /// 存的是 "station" 这类小写键，直接按 "Station" 取会静默落到默认值，故命中失败时再忽略大小写找一次。
    /// </summary>
    public static string? GetProperty(DeviceConnectionConfig config, string key)
    {
        if (config.ExtendedProperties.TryGetValue(key, out var value)) return value;
        foreach (var pair in config.ExtendedProperties)
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        return null;
    }

    private static Area[] GetAreas(InovanceSeries series) => Areas.TryGetValue(series, out var areas) ? areas : Areas[InovanceSeries.H3U];
    private static bool IsSupportedByHsl(InovanceSeries series, string address) => new byte[] { 1, 2, 3, 4, 5, 6, 15, 16 }.Any(code => InovanceHelper.PraseInovanceAddress(series, address, code).IsSuccess);
    private static Area ScalarArea(string root, bool octal = false) => new(raw => NormalizeScalar(raw, root, octal));
    private static Area PointArea(string root, bool octal = false) => new(raw => NormalizePoint(raw, root, octal));
    private static Area BitArea(string root, params string[] aliases) => new(raw => NormalizeBit(raw, root, aliases));
    private static string? NormalizeScalar(string raw, string root, bool octal)
    {
        var pattern = octal ? $@"^{root}(?<index>[0-7]+)$" : $@"^{root}(?<index>\d+)$";
        return Regex.IsMatch(raw, pattern, RegexOptions.IgnoreCase) ? raw : null;
    }

    private static string? NormalizePoint(string raw, string root, bool octal)
    {
        var pattern = octal ? $@"^{root}(?<word>[0-7]+)(?:\.(?<bit>\d+))?$" : $@"^{root}(?<word>\d+)(?:\.(?<bit>\d+))?$";
        var match = Regex.Match(raw, pattern, RegexOptions.IgnoreCase);
        return match.Success ? root + match.Groups["word"].Value + (match.Groups["bit"].Success ? "." + match.Groups["bit"].Value : string.Empty) : null;
    }
    private static string? NormalizeBit(string raw, string root, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            var bit = Regex.Match(raw, $@"^{alias}(?<word>\d+)\.(?<bit>\d+)$", RegexOptions.IgnoreCase);
            if (bit.Success) return $"{root}{bit.Groups["word"].Value}.{bit.Groups["bit"].Value}";
            var word = Regex.Match(raw, $@"^{alias}(?<word>\d+)$", RegexOptions.IgnoreCase);
            if (word.Success) return $"{root}{word.Groups["word"].Value}.0";
        }
        return null;
    }

}
