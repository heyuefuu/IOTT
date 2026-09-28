using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace MachineConnectionApi.Services;

public static class EvaluationSpreadsheetReader
{
    private const long MaxExpandedBytes = 20 * 1024 * 1024;
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static List<List<string>> Read(Stream source)
    {
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > 200 || zip.Entries.Sum(entry => entry.Length) > MaxExpandedBytes)
            throw new ArgumentException("Excel 文件解压后过大。");
        var workbook = Load(zip, "xl/workbook.xml");
        var sheet = workbook.Descendants(Main + "sheet").FirstOrDefault()
            ?? throw new ArgumentException("Excel 没有工作表。");
        var relationshipId = (string?)sheet.Attribute(Rel + "id");
        var relationships = Load(zip, "xl/_rels/workbook.xml.rels");
        var relationship = relationships.Root?.Elements().FirstOrDefault(element => (string?)element.Attribute("Id") == relationshipId);
        var target = (string?)relationship?.Attribute("Target");
        if (string.IsNullOrWhiteSpace(target) || (string?)relationship?.Attribute("TargetMode") == "External"
            || target.Contains("..", StringComparison.Ordinal) || target.Contains('\\'))
            throw new ArgumentException("Excel 工作表引用无效。");
        var sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        var sharedEntry = zip.GetEntry("xl/sharedStrings.xml");
        var shared = sharedEntry is null ? [] : ReadXml(sharedEntry).Descendants(Main + "si")
            .Select(element => string.Concat(element.Descendants(Main + "t").Select(text => text.Value))).ToList();
        var document = Load(zip, sheetPath);
        var result = new List<List<string>>();
        foreach (var row in document.Descendants(Main + "row"))
        {
            if (result.Count > 1000) throw new ArgumentException("每次最多导入 1000 条记录。");
            var values = new List<string>();
            foreach (var cell in row.Elements(Main + "c"))
            {
                if (cell.Element(Main + "f") is not null) throw new ArgumentException("导入单元格应填写实际数值，不接受公式。");
                var reference = (string?)cell.Attribute("r") ?? throw new ArgumentException("Excel 单元格坐标缺失。");
                var column = ColumnIndex(reference);
                while (values.Count <= column) values.Add("");
                var value = cell.Element(Main + "v")?.Value ?? "";
                var kind = (string?)cell.Attribute("t");
                if (kind == "inlineStr") value = string.Concat(cell.Descendants(Main + "t").Select(text => text.Value));
                else if (kind == "s")
                {
                    if (!int.TryParse(value, out var sharedIndex) || sharedIndex < 0 || sharedIndex >= shared.Count)
                        throw new ArgumentException("Excel 共享文本索引无效。");
                    value = shared[sharedIndex];
                }
                if (value.Length > 32767) throw new ArgumentException("Excel 单元格文本过长。");
                values[column] = value;
            }
            result.Add(values);
        }
        return result;
    }

    private static int ColumnIndex(string reference)
    {
        var column = 0;
        foreach (var letter in reference.TakeWhile(char.IsLetter))
        {
            if (letter < 'A' || letter > 'Z') throw new ArgumentException("Excel 单元格坐标无效。");
            column = checked(column * 26 + letter - 'A' + 1);
            if (column > 2000) throw new ArgumentException("Excel 列数过多。");
        }
        if (column == 0) throw new ArgumentException("Excel 单元格坐标无效。");
        return column - 1;
    }

    private static XDocument Load(ZipArchive archive, string name) =>
        ReadXml(archive.GetEntry(name) ?? throw new ArgumentException("不是受支持的 XLSX 文件，缺少 " + name));

    private static XDocument ReadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxExpandedBytes,
        });
        return XDocument.Load(reader);
    }
}
