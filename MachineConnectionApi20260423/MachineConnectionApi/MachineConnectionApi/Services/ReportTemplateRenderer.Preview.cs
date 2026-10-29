using System.Net;
using System.Text;

namespace MachineConnectionApi.Services;

public static partial class ReportTemplateRenderer
{
    public static string Preview(string content)
    {
        var original = new ReportTemplateDocument("示例报告（非实测）", ["以下为模板示例数据，不代表设备实测或验收结论。"],
            [new("示例证据表（非实测）", ["指标", "得分", "证据"], [new[] { "示例指标", "0", "示例证据，非实测" }])],
            ["示例结论（非实测）：待正式运行后填写。"]);
        var values = new Dictionary<string, string>
        {
            ["TaskName"] = "示例任务（非实测）", ["MachineName"] = "示例机床（非实测）", ["TotalScore"] = "0（示例）",
            ["Conclusion"] = "示例结论（非实测）", ["RunId"] = "SAMPLE-RUN", ["MachineNo"] = "SAMPLE-001",
            ["MachineModel"] = "示例型号", ["ControlSystem"] = "示例数控系统", ["TestDate"] = "示例日期",
            ["Tester"] = "示例测试人员", ["Result"] = "待验收（示例）"
        };
        return Html(Render(content, original, values));
    }

    private static string Html(ReportTemplateDocument report)
    {
        var html = new StringBuilder("<h1>").Append(Esc(report.Title)).Append("</h1>");
        Paragraphs(html, report.Introduction);
        foreach (var table in report.Tables)
        {
            html.Append("<h2>").Append(Esc(table.Title)).Append("</h2><table><thead><tr>");
            foreach (var header in table.Headers) html.Append("<th>").Append(Esc(header)).Append("</th>");
            html.Append("</tr></thead><tbody>");
            foreach (var row in table.Rows)
            {
                html.Append("<tr>");
                foreach (var cell in row) html.Append("<td>").Append(Esc(cell)).Append("</td>");
                html.Append("</tr>");
            }
            html.Append("</tbody></table>");
        }
        Paragraphs(html, report.Closing);
        return html.ToString();
    }

    private static void Paragraphs(StringBuilder html, IEnumerable<string> paragraphs)
    {
        var inList = false;
        foreach (var paragraph in paragraphs)
        {
            var line = paragraph.Trim();
            var bullet = line.StartsWith("- ", StringComparison.Ordinal);
            if (inList && !bullet) { html.Append("</ul>"); inList = false; }
            if (bullet)
            {
                if (!inList) { html.Append("<ul>"); inList = true; }
                html.Append("<li>").Append(Esc(line[2..])).Append("</li>");
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal)) html.Append("<h3>").Append(Esc(line[4..])).Append("</h3>");
            else if (line.StartsWith("## ", StringComparison.Ordinal)) html.Append("<h2>").Append(Esc(line[3..])).Append("</h2>");
            else if (line.Length > 0) html.Append("<p>").Append(Esc(paragraph).Replace("\n", "<br>" )).Append("</p>");
        }
        if (inList) html.Append("</ul>");
    }

    private static string Esc(string value) => WebUtility.HtmlEncode(value);
}
