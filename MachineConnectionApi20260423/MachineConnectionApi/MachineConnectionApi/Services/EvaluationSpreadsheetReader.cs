using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ExcelDataReader;
using ExcelDataReader.Exceptions;

namespace MachineConnectionApi.Services;

public static class EvaluationSpreadsheetReader
{
    public const int MaxInputBytes = 5 * 1024 * 1024;
    public const int MaxRows = 1001; // Header plus at most 1000 records; physical row numbers are preserved.
    public const int MaxColumns = 2000;
    public const int MaxCellCharacters = 32767;
    private const long MaxExpandedBytes = 20 * 1024 * 1024;
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static List<List<string>> Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Do not trust IFormFile.Length or require a seekable HTTP stream. Both formats have the same byte budget.
        using var input = CopyBounded(source);
        var signature = input.GetBuffer().AsSpan(0, (int)Math.Min(input.Length, 8));
        if (signature.Length >= 4 && signature[0] == 0x50 && signature[1] == 0x4b)
            return ReadOpenXml(input);
        if (signature.SequenceEqual(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 })
            || signature.Length >= 4 && signature[0] == 0x09 && signature[1] is 0x00 or 0x02 or 0x04 or 0x08)
            return ReadBinary(input);
        throw new ArgumentException("文件内容不是受支持的 XLS（二进制 BIFF）或 XLSX 文件。");
    }

    private static MemoryStream CopyBounded(Stream source)
    {
        var copy = new MemoryStream();
        try
        {
            var buffer = new byte[81920];
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (copy.Length + count > MaxInputBytes) throw new ArgumentException("Excel 文件不能超过 5MiB。");
                copy.Write(buffer, 0, count);
            }
            copy.Position = 0;
            return copy;
        }
        catch { copy.Dispose(); throw; }
    }

    private static List<List<string>> ReadBinary(MemoryStream source)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            // Formula caches are not evidence. The public reader returns caches without provenance,
            // so validate and read the SAME extracted BIFF stream (including BOUNDSHEET offsets).
            using var workbook = EvaluationBiffGuard.OpenValuesOnlyWorkbook(source);
            // Binary-only entry point: XLSX remains on our DTD-disabled, expansion-bounded XML reader.
            using var reader = ExcelReaderFactory.CreateBinaryReader(workbook, new ExcelReaderConfiguration { LeaveOpen = true });
            ValidateDimensions(reader.RowCount, reader.FieldCount);
            var rows = new List<List<string>>();
            long characters = 0;
            while (reader.Read())
            {
                ValidateDimensions(rows.Count + 1, reader.FieldCount);
                var row = new List<string>(reader.FieldCount);
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    var value = reader.GetValue(column) switch
                    {
                        null => "",
                        DateTime date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture) ?? "",
                        var text => text.ToString() ?? "",
                    };
                    CheckText(value, ref characters);
                    row.Add(value);
                }
                rows.Add(row);
            }
            return rows;
        }
        catch (InvalidPasswordException error)
        {
            throw new ArgumentException("不支持加密或需要密码的 XLS 文件，请在 Excel 中解除加密后重新导入。", error);
        }
        catch (Exception error) when (error is ExcelReaderException or IOException or InvalidDataException or FormatException
            or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException or NotSupportedException)
        {
            throw new ArgumentException("XLS 文件损坏、已加密或不是受支持的二进制工作簿，请另存为未加密的 XLS/XLSX 后重试。", error);
        }
    }

    private static List<List<string>> ReadOpenXml(Stream source)
    {
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > 200 || zip.Entries.Any(entry => entry.Length > MaxExpandedBytes)
            || zip.Entries.Sum(entry => entry.Length) > MaxExpandedBytes)
            throw new ArgumentException("Excel 文件解压后过大。");
        if (zip.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != zip.Entries.Count)
            throw new ArgumentException("Excel 包含重复的压缩包条目。");
        var workbook = Load(zip, "xl/workbook.xml");
        var sheet = workbook.Descendants(Main + "sheet").FirstOrDefault()
            ?? throw new ArgumentException("Excel 没有工作表。");
        var relationshipId = (string?)sheet.Attribute(Rel + "id");
        var relationships = Load(zip, "xl/_rels/workbook.xml.rels");
        var relationship = relationships.Root?.Elements().FirstOrDefault(element => (string?)element.Attribute("Id") == relationshipId);
        var target = (string?)relationship?.Attribute("Target");
        if (string.IsNullOrWhiteSpace(target) || string.Equals((string?)relationship?.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)
            || target.Contains("..", StringComparison.Ordinal) || target.Contains('\\') || target.Contains(':'))
            throw new ArgumentException("Excel 工作表引用无效。");
        var sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        var sharedEntry = zip.GetEntry("xl/sharedStrings.xml");
        var shared = sharedEntry is null ? [] : ReadXml(sharedEntry).Descendants(Main + "si")
            .Select(element => string.Concat(element.Descendants(Main + "t").Select(text => text.Value))).ToList();
        foreach (var text in shared) CheckText(text);
        var document = Load(zip, sheetPath);
        var result = new List<List<string>>();
        long characters = 0;
        foreach (var row in document.Descendants(Main + "row"))
        {
            var rowNumber = result.Count + 1;
            if (row.Attribute("r") is { } number && (!int.TryParse(number.Value, NumberStyles.None, CultureInfo.InvariantCulture, out rowNumber)
                || rowNumber <= result.Count)) throw new ArgumentException("Excel 行坐标无效或重复。");
            ValidateDimensions(rowNumber, 0);
            while (result.Count < rowNumber - 1) result.Add([]);
            var values = new List<string>();
            var seenColumns = new HashSet<int>();
            foreach (var cell in row.Elements(Main + "c"))
            {
                if (cell.Element(Main + "f") is not null) throw new ArgumentException(EvaluationBiffGuard.FormulaMessage);
                var reference = (string?)cell.Attribute("r") ?? throw new ArgumentException("Excel 单元格坐标缺失。");
                var column = ColumnIndex(reference, rowNumber);
                if (!seenColumns.Add(column)) throw new ArgumentException("Excel 单元格坐标重复。");
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
                CheckText(value, ref characters);
                values[column] = value;
            }
            result.Add(values);
        }
        return result;
    }

    private static void ValidateDimensions(int rows, int columns)
    {
        if (rows > MaxRows) throw new ArgumentException("每次最多导入 1000 条记录（另加一行表头）。");
        if (columns > MaxColumns) throw new ArgumentException("Excel 列数过多，最多支持 2000 列。");
    }

    private static void CheckText(string value)
    {
        if (value.Length > MaxCellCharacters) throw new ArgumentException("Excel 单元格文本过长，最多支持 32767 个字符。");
    }

    private static void CheckText(string value, ref long characters)
    {
        CheckText(value);
        characters += value.Length;
        // A short shared string table can be referenced millions of times in either format.
        if (characters > MaxExpandedBytes / 2) throw new ArgumentException("Excel 单元格文本总量过大。");
    }

    private static int ColumnIndex(string reference, int rowNumber)
    {
        var column = 0;
        var letters = 0;
        foreach (var letter in reference.TakeWhile(char.IsLetter))
        {
            if (letter < 'A' || letter > 'Z') throw new ArgumentException("Excel 单元格坐标无效。");
            column = checked(column * 26 + letter - 'A' + 1);
            ValidateDimensions(0, column);
            letters++;
        }
        if (column == 0 || !int.TryParse(reference.AsSpan(letters), NumberStyles.None, CultureInfo.InvariantCulture, out var referencedRow)
            || referencedRow != rowNumber) throw new ArgumentException("Excel 单元格坐标无效。");
        return column - 1;
    }

    private static XDocument Load(ZipArchive archive, string name) =>
        ReadXml(archive.GetEntry(name) ?? throw new ArgumentException("不是受支持的 XLSX 文件，缺少 " + name));

    private static XDocument ReadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (bounded.Length + count > entry.Length || bounded.Length + count > MaxExpandedBytes)
                throw new ArgumentException("Excel 压缩条目的实际解压大小超限或与声明不符。");
            bounded.Write(buffer, 0, count);
        }
        if (bounded.Length != entry.Length) throw new InvalidDataException("Excel 压缩条目不完整。");
        bounded.Position = 0;
        using var reader = XmlReader.Create(bounded, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxExpandedBytes,
        });
        return XDocument.Load(reader);
    }
}
