using System.Net;
using System.Text;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

public interface ICsParallelReportService
{
    CsReportFile Generate(CsParallelReportRequest request, string format);
}

public sealed partial class CsParallelReportService : ICsParallelReportService
{
    public CsReportFile Generate(CsParallelReportRequest request, string format)
    {
        if (request is null || request.Request is null || request.Result is null)
            throw new ArgumentException("报告数据不能为空");

        var normalized = (format ?? "").Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(request.TemplateId))
        {
            if (normalized is not ("pdf" or "docx"))
                throw new ArgumentException("自定义报告模板仅支持 PDF 或 Word 导出。");
            var document = ReportTemplateRenderer.Apply(request.TemplateId,
                new("并行连接验证报告", BuildPdfLines(request).Skip(1).TakeWhile(line => line != "失败明细:").ToArray(),
                    [new("失败明细", ["时间", "设备/IP", "错误信息"],
                        request.Result.Failures.Count == 0 ? [new[] { "—", "—", "无失败记录" }]
                            : request.Result.Failures.Select(failure => (IReadOnlyList<string>)new[]
                                { Text(failure.Time), Text(failure.DeviceIp), Text(failure.Error) }).ToArray(), [20, 25, 55])], []),
                new Dictionary<string, string>
                {
                    ["TaskName"] = "并行连接验证", ["MachineName"] = request.Request.StartIp,
                    ["Result"] = $"{request.Result.SuccessRate}%", ["TestDate"] = request.Result.FinishedAt,
                    ["Conclusion"] = $"成功 {request.Result.Success} / {request.Result.Total}，失败 {request.Result.Failure}"
                });
            return new(FileName(normalized), normalized == "pdf" ? "application/pdf" : EvaluationDocxBuilder.ContentType,
                normalized == "pdf" ? ReportTemplateRenderer.Pdf(document) : ReportTemplateRenderer.Docx(document));
        }
        return normalized switch
        {
            "html" => new CsReportFile(FileName("html"), "text/html; charset=utf-8",
                Encoding.UTF8.GetBytes(BuildHtml(request))),
            "pdf" => new CsReportFile(FileName("pdf"), "application/pdf", BuildPdf(request)),
            // Word 不受 PDF 单页 44 行限制，失败明细全部写入
            "docx" => new CsReportFile(FileName("docx"), EvaluationDocxBuilder.ContentType,
                EvaluationDocxBuilder.BuildReport("并行连接验证报告", BuildPdfLines(request).Skip(1), [])),
            _ => throw new ArgumentException("报告格式仅支持 pdf、html 或 docx")
        };
    }

    private static string BuildHtml(CsParallelReportRequest report)
    {
        var b = new StringBuilder();
        b.AppendLine("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        b.AppendLine("<title>并行连接验证报告</title>");
        b.AppendLine("<style>body{font-family:Arial,'Microsoft YaHei',sans-serif;margin:32px;color:#1f2937}h1{font-size:24px}table{border-collapse:collapse;width:100%;margin:14px 0 24px}th,td{border:1px solid #d1d5db;padding:8px 10px;text-align:left}th{background:#f3f4f6}.ok{color:#15803d}.bad{color:#b91c1c}</style>");
        b.AppendLine("</head><body><h1>并行连接验证报告</h1>");
        b.AppendLine("<h2>测试配置</h2><table><tbody>");
        AppendRow(b, "协议", report.Request.Protocol);
        AppendRow(b, "目标地址", $"{report.Request.StartIp}:{report.Request.Port}");
        AppendRow(b, "模拟设备数量", report.Request.DeviceCount.ToString());
        AppendRow(b, "并发连接数", report.Request.ConcurrentCount.ToString());
        AppendRow(b, "连接模式", ModeText(report));
        AppendRow(b, "测试时长", report.DurationSeconds > 0 ? $"{report.DurationSeconds} 秒" : "-");
        AppendRow(b, "超时时间", $"{report.Request.TimeoutMs} ms");
        if (IsMqtt(report.Request.Protocol))
        {
            AppendRow(b, "MQTT TLS", report.Request.MqttUseTls ? "启用" : "关闭");
            AppendRow(b, "MQTT ClientId", Text(report.Request.MqttClientId));
            AppendRow(b, "MQTT 用户名", Text(report.Request.MqttUsername));
        }
        b.AppendLine("</tbody></table><h2>测试结果</h2><table><tbody>");
        AppendRow(b, "生成时间", Text(report.GeneratedAt));
        AppendRow(b, "完成时间", report.Result.FinishedAt);
        AppendRow(b, "总连接数", report.Result.Total.ToString());
        AppendRow(b, "成功数", report.Result.Success.ToString(), "ok");
        AppendRow(b, "失败数", report.Result.Failure.ToString(), report.Result.Failure > 0 ? "bad" : "ok");
        AppendRow(b, "成功率", $"{report.Result.SuccessRate}%");
        AppendRow(b, "平均响应时间", $"{report.Result.AvgRttMs:0.##} ms");
        AppendRow(b, "最大响应时间", $"{report.Result.MaxRttMs:0.##} ms");
        b.AppendLine("</tbody></table><h2>失败明细</h2><table><thead><tr><th>时间</th><th>设备/IP</th><th>错误信息</th></tr></thead><tbody>");
        if (report.Result.Failures.Count == 0)
        {
            b.AppendLine("<tr><td colspan=\"3\">无失败记录</td></tr>");
        }
        foreach (var failure in report.Result.Failures)
            b.Append("<tr><td>").Append(H(failure.Time)).Append("</td><td>").Append(H(failure.DeviceIp))
                .Append("</td><td>").Append(H(failure.Error)).AppendLine("</td></tr>");
        b.AppendLine("</tbody></table></body></html>");
        return b.ToString();
    }

    private static void AppendRow(StringBuilder b, string name, string? value, string? cls = null)
    {
        var classAttr = string.IsNullOrWhiteSpace(cls) ? "" : $" class=\"{cls}\"";
        b.Append("<tr><th>").Append(H(name)).Append("</th><td").Append(classAttr).Append(">")
            .Append(H(Text(value))).AppendLine("</td></tr>");
    }

    private static bool IsMqtt(string? protocol) => string.Equals(protocol, "MQTT", StringComparison.OrdinalIgnoreCase);

    private static string ModeText(CsParallelReportRequest report) =>
        string.IsNullOrWhiteSpace(report.ConnectionMode)
            ? report.Request.HoldMs > 0 ? "长连接" : "短连接"
            : report.ConnectionMode!;

    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string H(string? value) => WebUtility.HtmlEncode(Text(value));

    private static string FileName(string extension) => $"parallel-connection-report-{DateTime.Now:yyyyMMddHHmmss}.{extension}";
}
