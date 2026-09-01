namespace MachineConnectionApi.Services;

using System.IO.Compression;
using System.Security;
using System.Text;

/// <summary>
/// 无第三方依赖的最小 .xlsx 生成器（OOXML SpreadsheetML，单元格用 inlineStr）。
/// 满足验证报表导出：一张工作表 + 列宽设置，数字单元格按 number 写入以便 Excel 内计算。
/// </summary>
public static class ExcelBuilder
{
    public static byte[] Build(string sheetName, IReadOnlyList<IReadOnlyList<object?>> rows, IReadOnlyList<double>? columnWidths = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """);
            AddEntry(zip, "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            AddEntry(zip, "xl/workbook.xml",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="{Escape(sheetName)}" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            AddEntry(zip, "xl/_rels/workbook.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);
            AddEntry(zip, "xl/worksheets/sheet1.xml", BuildSheetXml(rows, columnWidths));
        }
        return buffer.ToArray();
    }

    private static string BuildSheetXml(IReadOnlyList<IReadOnlyList<object?>> rows, IReadOnlyList<double>? columnWidths)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        if (columnWidths is { Count: > 0 })
        {
            sb.Append("<cols>");
            for (var i = 0; i < columnWidths.Count; i++)
                sb.Append($"""<col min="{i + 1}" max="{i + 1}" width="{columnWidths[i]}" customWidth="1"/>""");
            sb.Append("</cols>");
        }
        sb.Append("<sheetData>");
        for (var r = 0; r < rows.Count; r++)
        {
            sb.Append($"""<row r="{r + 1}">""");
            var cells = rows[r];
            for (var c = 0; c < cells.Count; c++)
            {
                var reference = $"{ColumnName(c)}{r + 1}";
                var value = cells[c];
                if (value is int or long or double or float or decimal)
                {
                    sb.Append($"""<c r="{reference}"><v>{Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)}</v></c>""");
                }
                else
                {
                    var text = value?.ToString();
                    if (string.IsNullOrEmpty(text))
                        continue; // 空单元格（如“人工评分”留空列）直接省略
                    sb.Append($"""<c r="{reference}" t="inlineStr"><is><t xml:space="preserve">{Escape(text)}</t></is></c>""");
                }
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static string ColumnName(int index)
    {
        var name = "";
        index++;
        while (index > 0)
        {
            var rem = (index - 1) % 26;
            name = (char)('A' + rem) + name;
            index = (index - 1) / 26;
        }
        return name;
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? "";

    private static void AddEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
