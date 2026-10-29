using System.Text.RegularExpressions;

namespace MachineConnectionApi.Services;

public sealed record ReportTemplateDocument(string Title, IReadOnlyList<string> Introduction,
    IReadOnlyList<EvaluationPdfTable> Tables, IReadOnlyList<string> Closing, bool Markdown = false);

public static partial class ReportTemplateRenderer
{
    private static readonly Regex Token = new(@"\{\{\s*([A-Za-z][A-Za-z0-9]*)\s*\}\}",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly string[] Blocks = ["Introduction", "Tables", "Closing"];
    private static readonly string[] Variables = ["Title", "TaskName", "MachineName", "TotalScore", "Conclusion",
        "RunId", "MachineNo", "MachineModel", "ControlSystem", "TestDate", "Tester", "Result", "GeneratedAt"];

    public static void Validate(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 20000)
            throw new ArgumentException("模板内容须为 1–20000 个字符。");
        var lines = Lines(content);
        var found = new List<string>();
        foreach (Match match in Token.Matches(content))
        {
            var name = match.Groups[1].Value;
            if (!Blocks.Contains(name) && !Variables.Contains(name))
                throw new ArgumentException($"未知模板变量：{name}。");
        }
        var literals = Token.Replace(content, "");
        if (literals.Contains("{{", StringComparison.Ordinal) || literals.Contains("}}", StringComparison.Ordinal))
            throw new ArgumentException("模板变量语法无效，请使用 {{Variable}}。");
        var first = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith("# ", StringComparison.Ordinal) && index != first)
                throw new ArgumentException("一级标题只能位于模板第一行，章节使用 ## 或 ###。");
            foreach (var block in Blocks)
            {
                var matches = Token.Matches(line).Where(match => match.Groups[1].Value == block).ToArray();
                if (matches.Length == 0) continue;
                if (matches.Length != 1 || matches[0].Value != line)
                    throw new ArgumentException("{{" + block + "}} 必须独占一行。");
                found.Add(block);
            }
        }
        if (!found.SequenceEqual(Blocks))
            throw new ArgumentException("模板必须按顺序各包含一次 {{Introduction}}、{{Tables}}、{{Closing}}，以保留原始报告与证据。");
    }

    public static ReportTemplateDocument Apply(string? templateId, ReportTemplateDocument original,
        IReadOnlyDictionary<string, string>? values = null)
    {
        if (string.IsNullOrWhiteSpace(templateId)) return original;
        return Render(ReportTemplateStore.Default.Require(templateId).Content, original, values);
    }

    public static ReportTemplateDocument Render(string content, ReportTemplateDocument original,
        IReadOnlyDictionary<string, string>? values = null)
    {
        Validate(content);
        var context = Variables.ToDictionary(name => name, _ => "—", StringComparer.Ordinal);
        context["Title"] = original.Title;
        context["GeneratedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
        if (values is not null)
            foreach (var (name, value) in values)
                if (context.ContainsKey(name)) context[name] = string.IsNullOrWhiteSpace(value) ? "—" : value.Replace('\r', ' ').Replace('\n', ' ');
        string Replace(string line) => Token.Replace(line, match => context[match.Groups[1].Value]);
        var title = original.Title;
        var before = new List<string>();
        var after = new List<string>();
        var target = before;
        var first = true;
        foreach (var raw in Lines(content))
        {
            var line = raw.Trim();
            if (first && line.Length == 0) continue;
            if (first && line.StartsWith("# ", StringComparison.Ordinal))
            { title = Replace(line[2..]); first = false; continue; }
            first = false;
            // Parse blocks before substituting data; values containing {{...}} are never re-expanded.
            var block = Token.Match(line);
            if (block.Success && block.Value == line && Blocks.Contains(block.Groups[1].Value))
            {
                switch (block.Groups[1].Value)
                {
                    case "Introduction": target.AddRange(original.Introduction); break;
                    case "Tables": target = after; break;
                    case "Closing": target.AddRange(original.Closing); break;
                }
            }
            else target.Add(Replace(raw));
        }
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw new ArgumentException("模板渲染后的报告标题须为 1–200 个字符。");
        return new(title, before, original.Tables, after, Markdown: true);
    }

    private static string[] Lines(string content) => content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
