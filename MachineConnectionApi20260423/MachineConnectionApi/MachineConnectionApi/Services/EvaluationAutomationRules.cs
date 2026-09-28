namespace MachineConnectionApi.Services;

using MachineConnectionApi.Models;

/// <summary>Pure rule validation and scoring. A rule is not evidence that a measurement was performed.</summary>
public static class EvaluationAutomationRules
{
    private static readonly HashSet<string> ReadDataTypes = new(StringComparer.Ordinal)
    {
        "Bool", "Int8", "UInt8", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Float", "Double", "String", "ByteArray",
    };

    public static EvaluationAutomationRule? DefaultFor(string metricId) => metricId switch
    {
        "industrial-protocol" => new() { Unit = "%", ScoringMode = "linear" },
        "communication-stability" => Bands("min", (30, 100), (20, 80), (10, 60), (0, 0)),
        "max-connections" => Bands("count", (4, 100), (3, 80), (2, 60), (0, 0)),
        "transfer-protocol" => Bands("count", (2, 100), (1, 60), (0, 0)),
        "file-integrity" => Bands("%", (100, 100), (75, 80), (50, 60), (0, 0)),
        "transfer-speed" => Bands("MB/s", (10, 100), (5, 80), (2, 60), (0, 0)),
        "file-size" => Bands("MB", (200, 100), (10, 80), (1, 60), (0, 0)),
        _ => null,
    };

    private static EvaluationAutomationRule Bands(string unit, params (double Min, double Score)[] bands) => new()
    {
        Unit = unit,
        ScoreBands = bands.Select(band => new EvaluationScoreBand { Min = band.Min, Score = band.Score }).ToList(),
    };

    public static void Validate(EvaluationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Automation is not { } rule) return;
        var defaults = DefaultFor(item.MetricId ?? "");
        if (defaults is null) throw new ArgumentException("只有明确关联的七项通讯指标可以配置自动测试规则。");
        if (!TryConvert(1, defaults.Unit, rule.Unit, out _)) throw new ArgumentException("测量单位与自动测试指标不兼容。");
        ValidateRule(rule);
    }

    private static void ValidateRule(EvaluationAutomationRule rule)
    {
        if (!TryUnit(rule.Unit, out var dimension, out _) || rule.ScoringMode is not ("linear" or "bands"))
            throw new ArgumentException("自动测试单位或评分模式无效。");
        if (rule.ScoreBands is null || rule.ScoreBands.Count > 100)
            throw new ArgumentException("评分档位无效，最多允许 100 档。");
        if (rule.ScoringMode == "linear" && (dimension != "percent" || rule.ScoreBands.Count != 0))
            throw new ArgumentException("线性评分仅用于百分比，且不能同时配置分档评分。");
        if (rule.ScoringMode == "bands" && rule.ScoreBands.Count == 0)
            throw new ArgumentException("请配置至少一个评分档位。");
        double? previousMin = null;
        double? previousScore = null;
        if (rule.ScoreBands.Any(band => band is null)) throw new ArgumentException("评分档位不能为空。");
        foreach (var band in rule.ScoreBands.OrderBy(band => band.Min))
        {
            if (!ValidValue(band.Min, rule.Unit) || !double.IsFinite(band.Score) || band.Score < 0 || band.Score > 100
                || previousMin == band.Min || previousScore > band.Score)
                throw new ArgumentException("档位下限须合法且不重复，分数须在 0 到 100 之间并随下限递增。");
            previousMin = band.Min;
            previousScore = band.Score;
        }
        if (rule.PassRule is { } pass && (pass.Comparison is not ("gte" or "eq" or "lte")
            || !ValidValue(pass.Threshold, pass.Unit) || !TryConvert(pass.Threshold, pass.Unit, rule.Unit, out _)))
            throw new ArgumentException("达标线须使用合法比较方式、有限数值及兼容单位；未配置时不能自动判通过。");
        var test = rule.Test;
        if (test is null || !double.IsFinite(test.DurationMinutes) || test.DurationMinutes <= 0 || test.DurationMinutes > 1440
            || !double.IsFinite(test.SampleIntervalSeconds) || test.SampleIntervalSeconds <= 0 || test.SampleIntervalSeconds > 86400
            || test.MaxConnections is < 1 or > 64 || test.FailureLimit is < 1 or > 100
            || test.ConcurrencyMode is not ("devices" or "connections" or "sessions"))
            throw new ArgumentException("测试时长应大于 0 且不超过 1440 分钟；间隔大于 0 且不超过 86400 秒；并发上限 1–64，失败次数 1–100。");
        if (test.ReadAddress is null || test.ReadAddress.Length > 500 || test.ReadAddress.Any(char.IsControl)
            || !ReadDataTypes.Contains(test.ReadDataType ?? ""))
            throw new ArgumentException("读取点位或数据类型无效。");
        if (string.IsNullOrWhiteSpace(test.TargetDirectory) || test.TargetDirectory.Length > 1024
            || test.TargetDirectory.Any(char.IsControl)
            || test.TargetDirectory.Replace('\\', '/').Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("目标目录不能为空、含控制字符或使用相对跳转路径。");
    }

    /// <summary>Value is expressed in rule.Unit. Invalid rules or measurements are unscored, never silently zero.</summary>
    public static double? Score(EvaluationAutomationRule rule, double value)
    {
        if (!ValidRule(rule) || !ValidValue(value, rule.Unit)) return null;
        if (rule.ScoringMode == "linear") return value;
        return rule.ScoreBands.Where(band => value >= band.Min).OrderByDescending(band => band.Min).FirstOrDefault()?.Score;
    }

    /// <summary>Pass/fail is independent of score. Null means no valid pass rule or measurement.</summary>
    public static bool? Judge(EvaluationAutomationRule rule, double value)
    {
        if (!ValidRule(rule) || !ValidValue(value, rule.Unit) || rule.PassRule is not { } pass
            || !TryConvert(value, rule.Unit, pass.Unit, out var converted)) return null;
        return pass.Comparison switch
        {
            "gte" => converted >= pass.Threshold,
            "lte" => converted <= pass.Threshold,
            "eq" => converted == pass.Threshold,
            _ => null,
        };
    }

    private static bool ValidRule(EvaluationAutomationRule? rule)
    {
        if (rule is null) return false;
        try { ValidateRule(rule); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool ValidValue(double value, string unit)
    {
        if (!double.IsFinite(value) || value < 0 || !TryUnit(unit, out var dimension, out _)) return false;
        return dimension switch { "percent" => value <= 100, "count" => value == Math.Truncate(value), _ => true };
    }

    /// <summary>Legacy MB/KB labels use binary byte units: 1 MB (MiB) = 1024 KB (KiB). No dimension guessing.</summary>
    public static bool TryConvert(double value, string fromUnit, string toUnit, out double converted)
    {
        converted = 0;
        if (!double.IsFinite(value) || !TryUnit(fromUnit, out var fromDimension, out var fromScale)
            || !TryUnit(toUnit, out var toDimension, out var toScale) || fromDimension != toDimension) return false;
        converted = value * (fromScale / toScale);
        return double.IsFinite(converted);
    }

    private static bool TryUnit(string? unit, out string dimension, out double scale)
    {
        (dimension, scale) = unit switch
        {
            "%" => ("percent", 1d),
            "count" => ("count", 1d),
            "s" => ("time", 1d),
            "min" => ("time", 60d),
            "h" => ("time", 3600d),
            "B" => ("bytes", 1d),
            "KB" or "KiB" => ("bytes", 1024d),
            "MB" or "MiB" => ("bytes", 1048576d),
            "B/s" => ("speed", 1d),
            "KB/s" or "KiB/s" => ("speed", 1024d),
            "MB/s" or "MiB/s" => ("speed", 1048576d),
            _ => ("", 0d),
        };
        return scale > 0;
    }

    public static string NormalizeProtocol(string protocol)
    {
        var key = new string((protocol ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return key switch
        {
            // These are the editor's protocol-family labels, not interchangeable connection modes.
            // In particular keep classic OPC, GskrmFileTransfer, SFTP/FTPS and robot SDKs separate.
            "OPCUA" or "OPCOPCUA" => "OPC UA",
            "MODBUS" or "MODBUSTCP" or "MODBUSRTU" => "Modbus",
            "NCLINK" or "NCLINKAPI" => "NC-Link",
            "GSK" or "GSKWEBSERVER" or "GSKRM" => "GSK",
            "ETHERNETIP" => "EtherNet/IP",
            "PROFINET" => "Profinet",
            "PROFIBUS" or "PROFIBUSDP" => "Profibus",
            "MTCONNECT" => "MTConnect",
            "SIEMENSS7" or "S7COMM" or "S7" => "S7",
            "FANUC" or "FANUCFOCAS" or "FOCAS" => "FOCAS",
            _ => key,
        };
    }
}
