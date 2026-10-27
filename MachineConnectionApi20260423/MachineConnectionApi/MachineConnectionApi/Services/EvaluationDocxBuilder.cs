namespace MachineConnectionApi.Services;

using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

/// <summary>
/// 无第三方依赖的最小 .docx 生成器（OOXML WordprocessingML）。
/// 与 <see cref="EvaluationPdfBuilder.BuildReport"/> 同构：标题、说明段落、带表头的表格、结尾段落；
/// 表头跨页重复，列宽按相对权重铺满 A4 版心。
/// </summary>
public static class EvaluationDocxBuilder
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    // A4 纵向，左右页边距 2 cm：11906 - 2 × 1134 = 9638 twips。
    private const int PageWidth = 11906, PageHeight = 16838, Margin = 1134, TextWidth = PageWidth - Margin * 2;

    public static byte[] BuildReport(string title, IEnumerable<string> introduction,
        IEnumerable<EvaluationPdfTable> tables, IEnumerable<string>? closing = null)
    {
        var body = new StringBuilder();
        Paragraph(body, title, style: "Title");
        foreach (var line in introduction) Paragraph(body, line);
        foreach (var table in tables) Table(body, table);
        if (closing is not null)
            foreach (var line in closing) Paragraph(body, line);
        body.Append(FormattableString.Invariant(
            $"""<w:sectPr><w:pgSz w:w="{PageWidth}" w:h="{PageHeight}"/><w:pgMar w:top="1134" w:right="{Margin}" w:bottom="1134" w:left="{Margin}" w:header="567" w:footer="567" w:gutter="0"/></w:sectPr>"""));

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                  <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
                </Types>
                """);
            AddEntry(zip, "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);
            AddEntry(zip, "word/_rels/document.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            AddEntry(zip, "word/styles.xml", Styles);
            AddEntry(zip, "word/document.xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>"""
                + body + "</w:body></w:document>");
        }
        return buffer.ToArray();
    }

    private static void Table(StringBuilder body, EvaluationPdfTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Headers.Count < 1) throw new ArgumentException("Word 表格至少需要 1 列。");
        if (table.Rows.Any(row => row.Count != table.Headers.Count))
            throw new ArgumentException("Word 表格数据列数与表头不一致。");
        var relative = table.ColumnWidths?.ToArray() ?? Enumerable.Repeat(1d, table.Headers.Count).ToArray();
        if (relative.Length != table.Headers.Count || relative.Any(value => !double.IsFinite(value) || value <= 0))
            throw new ArgumentException("Word 表格列宽无效。");
        var widths = relative.Select(value => (int)Math.Floor(TextWidth * value / relative.Sum())).ToArray();

        Paragraph(body, table.Title, style: "Heading2");
        body.Append("""<w:tbl><w:tblPr><w:tblStyle w:val="ReportTable"/><w:tblW w:w="0" w:type="auto"/><w:tblLayout w:type="fixed"/></w:tblPr><w:tblGrid>""");
        foreach (var width in widths) body.Append(CultureInfo.InvariantCulture, $"""<w:gridCol w:w="{width}"/>""");
        body.Append("</w:tblGrid>");
        Row(body, table.Headers, widths, header: true, shade: false);
        for (var index = 0; index < table.Rows.Count; index++)
            Row(body, table.Rows[index], widths, header: false, shade: index % 2 == 1);
        body.Append("</w:tbl>");
        Paragraph(body, "");
    }

    private static void Row(StringBuilder body, IReadOnlyList<string> cells, int[] widths, bool header, bool shade)
    {
        body.Append("<w:tr>");
        if (header) body.Append("<w:trPr><w:tblHeader/></w:trPr>");
        for (var column = 0; column < cells.Count; column++)
        {
            body.Append(CultureInfo.InvariantCulture, $"""<w:tc><w:tcPr><w:tcW w:w="{widths[column]}" w:type="dxa"/>""");
            if (header || shade)
                body.Append($"""<w:shd w:val="clear" w:color="auto" w:fill="{(header ? "E6E6E6" : "F7F7F7")}"/>""");
            body.Append("</w:tcPr>");
            Paragraph(body, cells[column], style: "TableText", bold: header);
            body.Append("</w:tc>");
        }
        body.Append("</w:tr>");
    }

    /// <summary>换行符拆成 w:br，保留与 PDF 相同的多行单元格效果。</summary>
    private static void Paragraph(StringBuilder body, string? text, string? style = null, bool bold = false)
    {
        body.Append("<w:p>");
        if (style is not null) body.Append($"""<w:pPr><w:pStyle w:val="{style}"/></w:pPr>""");
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        body.Append("<w:r>");
        if (bold) body.Append("<w:rPr><w:b/></w:rPr>");
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0) body.Append("<w:br/>");
            if (lines[index].Length > 0)
                body.Append("""<w:t xml:space="preserve">""").Append(Escape(lines[index])).Append("</w:t>");
        }
        body.Append("</w:r></w:p>");
    }

    // XML 1.0 不允许大部分控制字符；证据文本可能来自设备原始输出，需剔除后再转义。
    private static string Escape(string value)
    {
        var clean = new StringBuilder(value.Length);
        foreach (var character in value)
            if (character is '\t' or '\n' or '\r' || character >= 0x20 && character is not ((char)0xFFFE or (char)0xFFFF))
                clean.Append(character);
        return SecurityElement.Escape(clean.ToString()) ?? "";
    }

    private const string Styles =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:docDefaults>
            <w:rPrDefault><w:rPr><w:rFonts w:ascii="Arial" w:hAnsi="Arial" w:eastAsia="宋体" w:cs="Arial"/><w:sz w:val="21"/><w:szCs w:val="21"/><w:lang w:val="en-US" w:eastAsia="zh-CN"/></w:rPr></w:rPrDefault>
            <w:pPrDefault><w:pPr><w:spacing w:after="80" w:line="300" w:lineRule="auto"/></w:pPr></w:pPrDefault>
          </w:docDefaults>
          <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
          <w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:basedOn w:val="Normal"/><w:pPr><w:jc w:val="center"/><w:spacing w:after="240"/></w:pPr><w:rPr><w:rFonts w:eastAsia="黑体"/><w:b/><w:sz w:val="32"/><w:szCs w:val="32"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:pPr><w:keepNext/><w:spacing w:before="200" w:after="100"/><w:outlineLvl w:val="1"/></w:pPr><w:rPr><w:rFonts w:eastAsia="黑体"/><w:b/><w:sz w:val="24"/><w:szCs w:val="24"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="TableText"><w:name w:val="Table Text"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:after="0" w:line="260" w:lineRule="auto"/></w:pPr><w:rPr><w:sz w:val="18"/><w:szCs w:val="18"/></w:rPr></w:style>
          <w:style w:type="table" w:styleId="ReportTable"><w:name w:val="Report Table"/><w:tblPr><w:tblBorders><w:top w:val="single" w:sz="4" w:space="0" w:color="B3B3B3"/><w:left w:val="single" w:sz="4" w:space="0" w:color="B3B3B3"/><w:bottom w:val="single" w:sz="4" w:space="0" w:color="B3B3B3"/><w:right w:val="single" w:sz="4" w:space="0" w:color="B3B3B3"/><w:insideH w:val="single" w:sz="4" w:space="0" w:color="B3B3B3"/><w:insideV w:val="single" w:sz="4" w:space="0" w:color="B3B3B3"/></w:tblBorders><w:tblCellMar><w:top w:w="60" w:type="dxa"/><w:left w:w="100" w:type="dxa"/><w:bottom w:w="60" w:type="dxa"/><w:right w:w="100" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style>
        </w:styles>
        """;

    private static void AddEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
