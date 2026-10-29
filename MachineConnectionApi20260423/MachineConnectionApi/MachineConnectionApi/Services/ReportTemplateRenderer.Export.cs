namespace MachineConnectionApi.Services;

public static partial class ReportTemplateRenderer
{
    public static byte[] Pdf(ReportTemplateDocument report) => EvaluationPdfBuilder.BuildReport(
        report.Title, report.Introduction, report.Tables, report.Closing, report.Markdown);

    public static byte[] Docx(ReportTemplateDocument report) => EvaluationDocxBuilder.BuildReport(
        report.Title, report.Introduction, report.Tables, report.Closing, report.Markdown);
}
