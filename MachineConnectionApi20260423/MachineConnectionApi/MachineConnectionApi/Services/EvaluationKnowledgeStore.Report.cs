using System.Globalization;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

public sealed partial class EvaluationKnowledgeStore
{
    private const string KnowledgeScoringBasis = "知识库采用三级权重汇总，不等于验证任务所选项目的简单平均。二级小计 = Σ(项目评分 × 项目权重 / 同级项目权重合计)；分类得分 = Σ(二级小计 × 二级权重 / 100)；分类加权贡献 = 分类得分 × 一级权重 / 100；综合得分为分类加权贡献之和，最后保留一位小数。正权重项目未评分则所属汇总未评分；零权重贡献为 0；0 分是有效评分。评分与达标判据独立，得分不代表自动判定通过。";

    public byte[] ExportPdf(KnowledgeRecord record) => EvaluationPdfBuilder.BuildReport(
        "机床评价报告", ReportIntroduction(record), ReportTables(record),
        [$"综合评价结论：{record.Conclusion}", $"改进建议：{record.Suggestion}"]);

    private static IEnumerable<string> ReportIntroduction(KnowledgeRecord record)
    {
        yield return $"机床名称：{record.MachineName}   编号：{record.MachineNo}";
        yield return $"机床型号：{record.MachineModel}   数控系统：{record.ControlSystem}";
        yield return $"零件名称：{record.PartName}   零件特征：{record.PartFeature}";
        yield return $"测试地点：{record.TestLocation}";
        yield return $"测试日期：{record.TestDate}   测试人员：{record.Tester}";
        yield return $"评价类别：{record.Category}   数据来源：{record.DataSource}";
        yield return $"记录版本：{record.Version}   指标版本：{record.Snapshot.Version}";
        if (!string.IsNullOrEmpty(record.SyncRunId)) yield return $"来源任务：{record.SyncTaskId}   运行：{record.SyncRunId}";
        yield return $"综合得分：{ReportNumber(record.TotalScore)}";
        yield return $"评分口径：{KnowledgeScoringBasis}";
    }

    private static IEnumerable<EvaluationPdfTable> ReportTables(KnowledgeRecord record)
    {
        var summary = CategorySummaries(record);
        var categoryRows = summary.Select(category => (IReadOnlyList<string>)new[]
        {
            category.Section.Name, ReportNumber(category.Weight) + "%", ReportNumber(category.Score), ReportNumber(category.Contribution),
        }).ToList();
        categoryRows.Add(["综合得分", "", "", ReportNumber(record.TotalScore)]);
        yield return new("分类得分与总分贡献", ["一级分类", "权重", "分类得分", "总分贡献"], categoryRows, [3, 1, 1.5, 1.5]);

        var childRows = summary.SelectMany(category => category.Children.Select(child => (IReadOnlyList<string>)new[]
        {
            category.Section.Name, child.Child.Name, ReportNumber(child.Weight) + "%", ReportNumber(child.Score),
            ReportNumber(child.Contribution), ReportNumber(WeightedContribution(child.Contribution, category.Weight)),
        })).ToList();
        yield return new("二级小计与加权贡献", ["一级分类", "二级分类", "二级权重", "二级小计", "分类内贡献", "总分贡献"],
            childRows, [1.6, 2, 1, 1, 1.2, 1.2]);

        foreach (var category in summary)
            foreach (var child in category.Children)
            {
                var evidenceRows = new List<IReadOnlyList<string>>();
                string Evidence(string itemName, string field, string value)
                {
                    if (value.Length <= 350 && value.Count(character => character == '\n') < 8) return value;
                    var reference = $"证据 {evidenceRows.Count + 1}";
                    evidenceRows.Add([$"{reference} / {itemName}\n{field}", value]);
                    return $"详见后附{reference}";
                }
                var rows = child.Child.Items.Select(item => (IReadOnlyList<string>)new[]
                {
                    item.Name, ReportNumber(record.Scores.GetValueOrDefault(item.Id)),
                    ReportNumber(record.Weights.GetValueOrDefault(item.Id, item.Weight)) + "%",
                    Evidence(item.Name, "测试结果", record.TestResults.GetValueOrDefault(item.Id, "")),
                    Evidence(item.Name, "备注及证据", record.Remarks.GetValueOrDefault(item.Id, "")),
                }).ToList();
                yield return new($"{category.Section.Name} / {child.Child.Name}（小计 {ReportNumber(child.Score)}）",
                    ["评价项目", "评分", "项目权重", "测试结果", "备注及证据"], rows, [1.6, 0.7, 0.85, 2.8, 2.8]);
                if (evidenceRows.Count > 0)
                    yield return new($"{category.Section.Name} / {child.Child.Name} - 长文本证据",
                        ["证据编号 / 评价项目", "完整内容"], evidenceRows, [24, 76]);
            }
    }

    // Match Prepare's persisted scoring formula exactly, including item-weight rounding tolerance.
    // Summary fields are derived from the record's immutable snapshot, never the current indicator library.
    private static List<CategorySummary> CategorySummaries(KnowledgeRecord record) => record.Snapshot.Indicators.Select(section =>
    {
        var children = section.Children.Select(child =>
        {
            var siblingWeight = child.Items.Sum(item => record.Weights.GetValueOrDefault(item.Id, item.Weight));
            var score = SumScores(child.Items.Select(item => WeightedContribution(record.Scores.GetValueOrDefault(item.Id),
                siblingWeight > 0 ? record.Weights.GetValueOrDefault(item.Id, item.Weight) * 100 / siblingWeight : 0)));
            var weight = record.SubCategoryWeights.GetValueOrDefault(child.Id, child.Weight);
            return new ChildSummary(child, weight, score, WeightedContribution(score, weight));
        }).ToList();
        var score = SumScores(children.Select(child => child.Contribution));
        var weight = record.CategoryWeights.GetValueOrDefault(section.Id, section.Weight);
        return new CategorySummary(section, weight, score, WeightedContribution(score, weight), children);
    }).ToList();

    private static double? WeightedContribution(double? score, double weight) => weight == 0 ? 0 : score * weight / 100;
    private static double? SumScores(IEnumerable<double?> scores)
    {
        var values = scores.ToList();
        return values.Any(value => value is null) ? null : values.Sum(value => value!.Value);
    }
    private static string ReportNumber(double? number) => number?.ToString("0.##", CultureInfo.InvariantCulture) ?? "未评分";
    private sealed record ChildSummary(EvaluationSubcategory Child, double Weight, double? Score, double? Contribution);
    private sealed record CategorySummary(EvaluationSection Section, double Weight, double? Score, double? Contribution, List<ChildSummary> Children);
}
