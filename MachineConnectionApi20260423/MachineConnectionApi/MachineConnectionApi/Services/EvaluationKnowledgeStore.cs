using System.Globalization;
using System.Text.Json;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

public sealed class KnowledgeConflictException(string message) : Exception(message);

public sealed partial class EvaluationKnowledgeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly JsonFileStore<KnowledgeRecord> _records;
    private readonly EvaluationIndicatorStore _indicators;
    private readonly IVerifyTaskStore _tasks;
    private readonly IDeviceStore _devices;

    public EvaluationKnowledgeStore(EvaluationIndicatorStore indicators, IVerifyTaskStore tasks,
        IDeviceStore devices, string fileName = "evaluation-records.json")
    {
        if (Path.GetFileName(fileName) != fileName) throw new ArgumentException("存储文件名无效。");
        _records = new(fileName);
        _indicators = indicators;
        _tasks = tasks;
        _devices = devices;
    }

    public List<KnowledgeRecord> List() => _records.ReadAll().OrderByDescending(row => row.CreatedAt).ToList();
    public KnowledgeRecord? Find(string id) => _records.ReadAll().FirstOrDefault(row => row.Id == id);

    public KnowledgeRecord Create(KnowledgeRecord input)
    {
        if (input is null || input.Snapshot?.Indicators is null) throw new ArgumentException("评价指标快照不能为空。");
        var snapshot = input.Snapshot.Indicators.Count > 0 ? input.Snapshot : _indicators.Get(input.Category);
        if (input.DataSource == "sync")
        {
            var draft = TaskDraft(input.SyncTaskId ?? "", input.Category);
            if (input.SyncRunId != draft.SyncRunId) throw new KnowledgeConflictException("任务结果已更新，请重新同步。");
            snapshot = draft.Snapshot;
        }
        var now = DateTimeOffset.UtcNow;
        var item = Prepare(input with { Id = Guid.NewGuid().ToString("N"), Snapshot = snapshot,
            Version = 1, CreatedAt = now, UpdatedAt = now });
        return _records.Update(rows =>
        {
            if (item.DataSource == "sync" && rows.Any(row => row.SyncTaskId == item.SyncTaskId && row.SyncRunId == item.SyncRunId))
                throw new KnowledgeConflictException("该任务运行结果已导入知识库。");
            rows.Add(item);
            return item;
        });
    }

    public KnowledgeRecord? Update(string id, KnowledgeRecord input) => _records.Update<KnowledgeRecord?>(rows =>
    {
        var index = rows.FindIndex(row => row.Id == id);
        if (index < 0) return null;
        var old = rows[index];
        if (old.Version != input.Version) throw new KnowledgeConflictException("该评价记录已更新，请刷新后重试。");
        var item = Prepare(input with { Id = id, Snapshot = old.Snapshot, Category = old.Category,
            Version = checked(old.Version + 1), CreatedAt = old.CreatedAt, UpdatedAt = DateTimeOffset.UtcNow,
            DataSource = old.DataSource, SyncTaskId = old.SyncTaskId, SyncRunId = old.SyncRunId });
        rows[index] = item;
        return item;
    });

    public bool Delete(string id) => _records.Update(rows => rows.RemoveAll(row => row.Id == id) > 0);

    public static KnowledgeRecord Prepare(KnowledgeRecord input)
    {
        if (input is null || input.Snapshot is null) throw new ArgumentException("评价指标快照不能为空。");
        EvaluationIndicatorStore.Validate(input.Snapshot);
        if (input.Category != input.Snapshot.Category) throw new ArgumentException("评价类别与指标快照不一致。");
        var required = new[] { input.MachineName, input.MachineNo, input.MachineModel,
            input.ControlSystem, input.PartName, input.TestDate };
        if (required.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("请填写机床名称、编号、型号、数控系统、零件名称和测试日期。");
        if (!DateOnly.TryParseExact(input.TestDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ArgumentException("测试日期格式应为 YYYY-MM-DD。");
        if (input.DataSource is not ("manual" or "sync" or "import")) throw new ArgumentException("数据来源无效。");
        if (input.CategoryWeights is null || input.SubCategoryWeights is null || input.Weights is null
            || input.Scores is null || input.TestResults is null || input.Remarks is null)
            throw new ArgumentException("评价评分数据不完整。");
        var sections = input.Snapshot.Indicators;
        var children = sections.SelectMany(section => section.Children).ToList();
        var items = children.SelectMany(child => child.Items).ToList();
        var categories = MergeWeights(sections.Select(section => (section.Id, section.Weight)), input.CategoryWeights);
        var subcategories = MergeWeights(children.Select(child => (child.Id, child.Weight)), input.SubCategoryWeights);
        var weights = MergeWeights(items.Select(item => (item.Id, item.Weight)), input.Weights);
        CheckSum(categories.Values, "一级分类");
        foreach (var section in sections)
        {
            CheckSum(section.Children.Select(child => subcategories[child.Id]), section.Name);
            foreach (var child in section.Children) CheckSum(child.Items.Select(item => weights[item.Id]), child.Name);
        }
        var ids = items.Select(item => item.Id).ToHashSet();
        if (input.Scores.Keys.Concat(input.TestResults.Keys).Concat(input.Remarks.Keys).Any(key => !ids.Contains(key)))
            throw new ArgumentException("评分数据包含不属于当前快照的指标。");
        if (input.Scores.Values.Any(score => score.HasValue && (!double.IsFinite(score.Value) || score < 0 || score > 100)))
            throw new ArgumentException("评分应为 0 到 100 之间的数字或留空。");
        if (input.TestResults.Values.Concat(input.Remarks.Values).Any(value => value is null || value.Length > 10000))
            throw new ArgumentException("测试结果及备注最多支持 10000 个字符。");
        double total = 0;
        var complete = true;
        foreach (var section in sections)
            foreach (var child in section.Children)
                foreach (var item in child.Items)
                {
                    var siblingWeight = child.Items.Sum(sibling => weights[sibling.Id]);
                    var factor = categories[section.Id] * subcategories[child.Id] * weights[item.Id] / (10000 * siblingWeight);
                    var score = input.Scores.GetValueOrDefault(item.Id);
                    if (factor > 0 && score is null) complete = false;
                    total += (score ?? 0) * factor;
                }
        return input with { CategoryWeights = categories, SubCategoryWeights = subcategories, Weights = weights,
            Scores = items.ToDictionary(item => item.Id, item => input.Scores.GetValueOrDefault(item.Id)),
            TotalScore = complete ? Math.Round(total, 1, MidpointRounding.AwayFromZero) : null };
    }

    private static Dictionary<string, double> MergeWeights(IEnumerable<(string Id, double Weight)> defaults,
        Dictionary<string, double> supplied)
    {
        var result = defaults.ToDictionary(item => item.Id, item => item.Weight);
        foreach (var pair in supplied)
        {
            if (!result.ContainsKey(pair.Key) || !double.IsFinite(pair.Value) || pair.Value < 0 || pair.Value > 100)
                throw new ArgumentException("权重应在 0 到 100 之间，且必须属于当前指标快照。");
            result[pair.Key] = pair.Value;
        }
        return result;
    }

    private static void CheckSum(IEnumerable<double> values, string name)
    {
        if (Math.Abs(values.Sum() - 100) > 0.10000001) throw new ArgumentException($"{name}的同级权重合计应为 100%。");
    }
}
