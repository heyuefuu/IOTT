namespace MachineConnectionApi.Services;

using System.Text.Json;
using MachineConnectionApi.Models;

public sealed class EvaluationConfigConflictException : Exception
{
    public EvaluationConfigConflictException() : base("评价指标配置已更新，请刷新后重试，当前修改可先导出保存。") { }
}

public sealed class EvaluationIndicatorStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AutomationMetricIds = new(StringComparer.Ordinal)
    {
        "industrial-protocol", "communication-stability", "max-connections", "transfer-protocol",
        "file-integrity", "transfer-speed", "file-size",
    };
    private readonly JsonFileStore<EvaluationConfig> _store;
    private readonly string _path;

    public EvaluationIndicatorStore() : this("evaluation-indicators.json") { }

    public EvaluationIndicatorStore(string fileName, string? seedPath = null)
    {
        if (Path.GetFileName(fileName) != fileName) throw new ArgumentException("存储文件名无效。", nameof(fileName));
        _path = Path.Combine(AppContext.BaseDirectory, "App_Data", fileName);
        _store = new JsonFileStore<EvaluationConfig>(fileName);
        _store.Update(rows =>
        {
            var sourcePath = File.Exists(_path) ? _path
                : seedPath ?? Path.Combine(AppContext.BaseDirectory, "SeedData", "evaluation-indicators.json");
            var sourceJson = File.ReadAllText(sourcePath);
            if (!File.Exists(_path))
            {
                var seed = JsonSerializer.Deserialize<List<EvaluationConfig>>(sourceJson, JsonOptions)
                    ?? throw new InvalidDataException("评价指标初始配置为空。");
                rows.AddRange(seed);
            }
            ValidateStored(rows);
            AddMissingAutomation(rows, sourceJson);
            ValidateStored(rows);
            return 0;
        });
    }

    public EvaluationConfig Get(string category)
    {
        ValidateCategory(category);
        var configs = _store.ReadAll();
        ValidateStored(configs);
        return configs.Single(config => config.Category == category);
    }

    public EvaluationConfig Save(EvaluationConfig config)
    {
        Validate(config);
        config = NormalizeWeights(config);
        return _store.Update(configs =>
        {
            ValidateStored(configs);
            var index = configs.FindIndex(current => current.Category == config.Category);
            if (configs[index].Version != config.Version) throw new EvaluationConfigConflictException();
            var saved = config with { Version = checked(config.Version + 1), UpdatedAt = DateTimeOffset.UtcNow.ToString("O") };
            configs[index] = saved;
            return saved;
        });
    }

    // Only migrate the live configuration. An explicit null disables automation; prose is never parsed.
    // Looking at the source JSON distinguishes a legacy missing field from that intentional null.
    private static void AddMissingAutomation(List<EvaluationConfig> configs, string sourceJson)
    {
        using var document = JsonDocument.Parse(sourceJson);
        var missing = new HashSet<(string Category, string ItemId)>();
        foreach (var config in document.RootElement.EnumerateArray())
        {
            if (!config.TryGetProperty("indicators", out var sections)) continue;
            foreach (var section in sections.EnumerateArray())
            {
                if (!section.TryGetProperty("children", out var children)) continue;
                foreach (var child in children.EnumerateArray())
                {
                    if (!child.TryGetProperty("items", out var items)) continue;
                    foreach (var item in items.EnumerateArray())
                        if (!item.TryGetProperty("automation", out _))
                            missing.Add((config.GetProperty("category").GetString()!, item.GetProperty("id").GetString()!));
                }
            }
        }

        for (var index = 0; index < configs.Count; index++)
        {
            var config = configs[index];
            configs[index] = config with
            {
                Indicators = config.Indicators.Select(section => section with
                {
                    Children = section.Children.Select(child => child with
                    {
                        Items = child.Items.Select(item => missing.Contains((config.Category, item.Id))
                            ? item with { Automation = EvaluationAutomationRules.DefaultFor(item.MetricId ?? "") }
                            : item).ToList(),
                    }).ToList(),
                }).ToList(),
            };
        }
    }

    // Near-balanced drafts absorb rounding in the last sibling, not on every render or read.
    // Larger discrepancies and adjustments which would make an invalid weight remain editable drafts.
    private static List<T> NormalizeSiblings<T>(List<T> nodes, Func<T, double> weight, Func<T, double, T> update)
    {
        if (nodes.Count == 0) return nodes;
        var difference = 100d - nodes.Sum(weight);
        if (difference == 0 || Math.Abs(difference) >= 2) return nodes;
        var last = Math.Round(weight(nodes[^1]) + difference, 10);
        if (last < 0 || last > 100) return nodes;
        var result = nodes.ToList();
        result[^1] = update(result[^1], last);
        return result;
    }

    private static EvaluationConfig NormalizeWeights(EvaluationConfig config) => config with
    {
        Indicators = NormalizeSiblings(config.Indicators.Select(section => section with
        {
            Children = NormalizeSiblings(section.Children.Select(child => child with
            {
                Items = NormalizeSiblings(child.Items, item => item.Weight, (item, weight) => item with { Weight = weight }),
            }).ToList(), child => child.Weight, (child, weight) => child with { Weight = weight }),
        }).ToList(), section => section.Weight, (section, weight) => section with { Weight = weight }),
    };

    public static void ValidateCategory(string category)
    {
        if (category is not ("machine" or "machining")) throw new ArgumentException("请选择机床类或加工中心类。");
    }

    private static void ValidateStored(List<EvaluationConfig> configs)
    {
        if (configs.Count != 2 || configs.Any(config => config is null)
            || configs.Select(config => config.Category).Distinct().Count() != 2)
            throw new InvalidDataException("评价指标存储数据不完整，请检查原始数据文件。");
        foreach (var config in configs) Validate(config);
    }

    private static void ValidateNode(string id, string name, double weight, HashSet<string> ids)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || !ids.Add(id)) throw new ArgumentException("指标标识不能为空或重复。");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) throw new ArgumentException("指标名称不能为空且不能超过 200 个字符。");
        if (!double.IsFinite(weight) || weight < 0 || weight > 100) throw new ArgumentException("指标权重应在 0 到 100 之间。");
    }

    private static void ValidateItem(EvaluationItem item, HashSet<string> ids)
    {
        if (item is null) throw new ArgumentException("评价项目不能为空。");
        ValidateNode(item.Id, item.Name, item.Weight, ids);
        if (item.EvidenceType is not ("standard" or "protocol" or "file")) throw new ArgumentException("测试依据类型无效。");
        if (item.Desc is null || item.Method is null || item.Standards is null || item.Evidence is null
            || item.Protocols is null || item.Protocols.Any(string.IsNullOrWhiteSpace)
            || item.Scoring is null || item.Scoring.Any(rule => string.IsNullOrWhiteSpace(rule.Key) || rule.Value is null)
            || item.Files is null) throw new ArgumentException("评价项目内容不完整。");
        EvaluationAutomationRules.Validate(item);
        if (item.ManualPassRule is { } manual)
        {
            if (item.MetricId is not null)
                throw new ArgumentException("自动测试项目请使用结构化规则中的达标阈值，不要同时配置人工阈值。");
            if (manual.Comparison is not ("gte" or "eq" or "lte") || !double.IsFinite(manual.Threshold)
                || string.IsNullOrWhiteSpace(manual.Unit) || manual.Unit.Length > 50 || manual.Unit.Any(char.IsControl))
                throw new ArgumentException("人工评价阈值应包含有效的比较方式、数值和单位（最多 50 个字符）。");
        }
        foreach (var file in item.Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Name) || file.Name.Length > 255 || file.Size is null
                || file.SizeBytes < 0 || (file.Id is not null && !Guid.TryParseExact(file.Id, "N", out _)))
                throw new ArgumentException("测试文件信息无效。");
        }
    }

    public static void Validate(EvaluationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ValidateCategory(config.Category);
        if (config.Version < 1 || config.Version == int.MaxValue) throw new ArgumentException("评价指标配置版本无效。");
        if (config.Indicators is null) throw new ArgumentException("评价指标分类列表不能为空。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var metricIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in config.Indicators)
        {
            if (section is null || section.Children is null) throw new ArgumentException("一级分类数据不完整。");
            ValidateNode(section.Id, section.Name, section.Weight, ids);
            foreach (var child in section.Children)
            {
                if (child is null || child.Items is null) throw new ArgumentException("二级分类数据不完整。");
                ValidateNode(child.Id, child.Name, child.Weight, ids);
                foreach (var item in child.Items)
                {
                    ValidateItem(item, ids);
                    if (item.MetricId is not null && (!AutomationMetricIds.Contains(item.MetricId) || !metricIds.Add(item.MetricId)))
                        throw new ArgumentException("自动测试指标关联无效，或在当前分类中重复关联。");
                }
            }
        }
    }
}
