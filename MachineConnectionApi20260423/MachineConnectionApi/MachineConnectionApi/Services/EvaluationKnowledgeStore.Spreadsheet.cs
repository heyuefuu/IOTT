using System.Globalization;
using System.Text;
using System.Text.Json;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

public sealed partial class EvaluationKnowledgeStore
{
    private static readonly string[] BaseColumns =
    [
        "机床名称", "机床编号", "机床型号", "数控系统", "零件名称", "零件特征",
        "测试地点", "测试日期", "测试人员", "评价类别", "综合评价结论", "改进建议", "综合得分", "指标快照JSON",
    ];

    private static List<string> SpreadsheetHeaders(EvaluationConfig config)
    {
        var headers = BaseColumns.ToList();
        foreach (var section in config.Indicators)
        {
            headers.Add($"分类权重:{section.Id}:{section.Name}");
            foreach (var child in section.Children)
            {
                headers.Add($"子分类权重:{child.Id}:{child.Name}");
                foreach (var item in child.Items)
                    foreach (var field in new[] { "评分", "权重", "测试结果", "备注" })
                        headers.Add($"{field}:{item.Id}:{item.Name}");
            }
        }
        foreach (var section in config.Indicators)
        {
            headers.Add($"分类得分:{section.Id}:{section.Name}");
            headers.Add($"分类加权贡献:{section.Id}:{section.Name}");
            foreach (var child in section.Children)
            {
                headers.Add($"二级小计:{child.Id}:{child.Name}");
                headers.Add($"二级分类内贡献:{child.Id}:{child.Name}");
                headers.Add($"二级总分贡献:{child.Id}:{child.Name}");
            }
        }
        headers.Add("评分口径");
        var snapshotParts = SnapshotParts(config);
        for (var index = 1; index < snapshotParts.Count; index++) headers.Add($"指标快照JSON分段:{index + 1}");
        return headers;
    }

    private static IReadOnlyList<object?> SpreadsheetRow(KnowledgeRecord record)
    {
        var snapshotParts = SnapshotParts(record.Snapshot);
        var row = new List<object?> { record.MachineName, record.MachineNo, record.MachineModel, record.ControlSystem,
            record.PartName, record.PartFeature, record.TestLocation, record.TestDate, record.Tester, record.Category,
            record.Conclusion, record.Suggestion, record.TotalScore, snapshotParts[0] };
        foreach (var section in record.Snapshot.Indicators)
        {
            row.Add(record.CategoryWeights.GetValueOrDefault(section.Id, section.Weight));
            foreach (var child in section.Children)
            {
                row.Add(record.SubCategoryWeights.GetValueOrDefault(child.Id, child.Weight));
                foreach (var item in child.Items)
                {
                    row.Add(record.Scores.GetValueOrDefault(item.Id));
                    row.Add(record.Weights.GetValueOrDefault(item.Id, item.Weight));
                    row.Add(record.TestResults.GetValueOrDefault(item.Id));
                    row.Add(record.Remarks.GetValueOrDefault(item.Id));
                }
            }
        }
        foreach (var section in CategorySummaries(record))
        {
            row.Add(section.Score);
            row.Add(section.Contribution);
            foreach (var child in section.Children)
            {
                row.Add(child.Score);
                row.Add(child.Contribution);
                row.Add(WeightedContribution(child.Contribution, section.Weight));
            }
        }
        row.Add(KnowledgeScoringBasis);
        row.AddRange(snapshotParts.Skip(1));
        return row;
    }

    private static List<string> SnapshotParts(EvaluationConfig snapshot)
    {
        // Rules and long evidence can exceed Excel's per-cell limit. Keep the original column
        // and append numbered continuation columns; old single-cell exports still import unchanged.
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        const int size = EvaluationSpreadsheetReader.MaxCellCharacters;
        return Enumerable.Range(0, (json.Length + size - 1) / size)
            .Select(index => json.Substring(index * size, Math.Min(size, json.Length - index * size))).ToList();
    }

    public byte[] Template(string category = "machine")
    {
        var snapshot = _indicators.Get(category);
        var record = new KnowledgeRecord { Category = category, Snapshot = snapshot };
        return ExcelBuilder.Build("评价记录", [SpreadsheetHeaders(snapshot).Cast<object?>().ToList(), SpreadsheetRow(record)]);
    }

    public byte[] ExportSpreadsheet(KnowledgeRecord record) => ExcelBuilder.Build("评价记录",
        [SpreadsheetHeaders(record.Snapshot).Cast<object?>().ToList(), SpreadsheetRow(record)]);

    public KnowledgeImportResult Import(Stream stream)
    {
        var rows = EvaluationSpreadsheetReader.Read(stream);
        if (rows.Count < 2) throw new ArgumentException("Excel 至少需要表头和一条评价记录。");
        var headers = rows[0];
        if (BaseColumns.Take(10).Any(name => !headers.Contains(name)))
            throw new ArgumentException("Excel 缺少必需列，请下载最新模板。");
        if (headers.Where(name => name.Length > 0).Distinct().Count() != headers.Count(name => name.Length > 0))
            throw new ArgumentException("Excel 存在重复表头。");
        var errors = new List<string>();
        var success = 0;
        var total = 0;
        foreach (var (row, index) in rows.Skip(1).Select((row, index) => (row, index + 2)))
        {
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            total++;
            try
            {
                var values = headers.Select((name, column) => (name, value: column < row.Count ? row[column] : ""))
                    .Where(pair => pair.name.Length > 0).ToDictionary(pair => pair.name, pair => pair.value);
                Create(ParseSpreadsheetRow(values));
                success++;
            }
            catch (Exception error) when (error is ArgumentException or JsonException or KnowledgeConflictException or FormatException)
            {
                errors.Add($"第 {index} 行：{error.Message}");
            }
        }
        return new(total, success, total - success, errors);
    }

    private KnowledgeRecord ParseSpreadsheetRow(Dictionary<string, string> values)
    {
        string Text(string name) => values.GetValueOrDefault(name, "").Trim();
        var category = Text("评价类别") is "" ? "machine" : Text("评价类别");
        var snapshotText = values.GetValueOrDefault("指标快照JSON", "");
        if (string.IsNullOrWhiteSpace(snapshotText)) snapshotText = "";
        var continuations = values.Where(pair => pair.Key.StartsWith("指标快照JSON分段:", StringComparison.Ordinal))
            .Select(pair => (Number: int.TryParse(pair.Key.AsSpan("指标快照JSON分段:".Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out var number) ? number : -1, pair.Value)).OrderBy(pair => pair.Number).ToList();
        var snapshotBuilder = new StringBuilder(snapshotText);
        for (var index = 0; index < continuations.Count; index++)
        {
            if (continuations[index].Number != index + 2 || snapshotText.Length == 0)
                throw new ArgumentException("指标快照 JSON 分段缺失或编号无效，请重新导出完整模板。");
            // Do not trim fragments: spaces inside a JSON string can span cell boundaries.
            snapshotBuilder.Append(continuations[index].Value);
        }
        snapshotText = snapshotBuilder.ToString();
        var snapshot = snapshotText.Length == 0 ? _indicators.Get(category)
            : JsonSerializer.Deserialize<EvaluationConfig>(snapshotText, JsonOptions) ?? throw new ArgumentException("指标快照为空。");
        var date = Text("测试日期");
        if (double.TryParse(date, NumberStyles.Number, CultureInfo.InvariantCulture, out var serialDate))
        {
            if (!double.IsFinite(serialDate) || serialDate < 1 || serialDate > 2958465) throw new ArgumentException("测试日期无效。");
            // Excel's 1900 date system inserts a fictitious 1900-02-29 (serial 60).
            if (serialDate is >= 60 and < 61) throw new ArgumentException("测试日期无效（Excel 序号 60 对应不存在的日期）。");
            date = DateTime.FromOADate(serialDate < 60 ? serialDate + 1 : serialDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        var result = new KnowledgeRecord { MachineName = Text("机床名称"), MachineNo = Text("机床编号"),
            MachineModel = Text("机床型号"), ControlSystem = Text("数控系统"), PartName = Text("零件名称"),
            PartFeature = Text("零件特征"), TestLocation = Text("测试地点"), TestDate = date, Tester = Text("测试人员"),
            Category = category, Snapshot = snapshot, DataSource = "import", Conclusion = Text("综合评价结论"), Suggestion = Text("改进建议") };
        foreach (var pair in values)
        {
            var parts = pair.Key.Split(':', 3);
            if (parts.Length < 2) continue;
            var key = parts[1];
            if (parts[0] == "测试结果") result.TestResults[key] = pair.Value;
            else if (parts[0] == "备注") result.Remarks[key] = pair.Value;
            else if (parts[0] is "分类权重" or "子分类权重" or "权重" or "评分")
            {
                if (string.IsNullOrWhiteSpace(pair.Value)) continue;
                if (!double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw new ArgumentException($"{pair.Key}应填写数字。");
                if (parts[0] == "评分") result.Scores[key] = number;
                else if (parts[0] == "分类权重") result.CategoryWeights[key] = number;
                else if (parts[0] == "子分类权重") result.SubCategoryWeights[key] = number;
                else result.Weights[key] = number;
            }
        }
        return result;
    }
}
