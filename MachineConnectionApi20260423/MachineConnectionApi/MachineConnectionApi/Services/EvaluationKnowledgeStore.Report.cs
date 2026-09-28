using System.Globalization;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

public sealed partial class EvaluationKnowledgeStore
{
    public byte[] ExportPdf(KnowledgeRecord record) => EvaluationPdfBuilder.Build(ReportLines(record));

    private static IEnumerable<string> ReportLines(KnowledgeRecord record)
    {
        yield return "机床评价报告";
        yield return $"机床名称：{record.MachineName}   编号：{record.MachineNo}";
        yield return $"机床型号：{record.MachineModel}   数控系统：{record.ControlSystem}";
        yield return $"零件名称：{record.PartName}   零件特征：{record.PartFeature}";
        yield return $"测试地点：{record.TestLocation}";
        yield return $"测试日期：{record.TestDate}   测试人员：{record.Tester}";
        yield return $"记录版本：{record.Version}   指标版本：{record.Snapshot.Version}";
        yield return $"综合得分：{record.TotalScore?.ToString("0.##", CultureInfo.InvariantCulture) ?? "待完成评分"}";
        yield return "";
        foreach (var section in record.Snapshot.Indicators)
        {
            yield return $"{section.Name}（权重 {record.CategoryWeights[section.Id]}%）";
            foreach (var child in section.Children)
            {
                yield return $"{child.Name}（权重 {record.SubCategoryWeights[child.Id]}%）";
                foreach (var item in child.Items)
                {
                    yield return $"{item.Name}：评分 {record.Scores.GetValueOrDefault(item.Id)?.ToString(CultureInfo.InvariantCulture) ?? "待评价"} / 权重 {record.Weights[item.Id]}%";
                    yield return $"测试结果：{record.TestResults.GetValueOrDefault(item.Id, "")}";
                    yield return $"备注：{record.Remarks.GetValueOrDefault(item.Id, "")}";
                }
            }
        }
        yield return "";
        yield return $"综合评价结论：{record.Conclusion}";
        yield return $"改进建议：{record.Suggestion}";
    }
}
