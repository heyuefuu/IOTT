using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MachineConnectionApi.Services;

public sealed record EvaluationPdfTable(string Title, IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<double>? ColumnWidths = null);

public static class EvaluationPdfBuilder
{
    // Kept for older exports, including their 46-line pagination contract.
    public static byte[] Build(IEnumerable<string> sourceLines)
    {
        var lines = sourceLines.SelectMany(line => Wrap(line, 44)).ToList();
        if (lines.Count == 0) lines.Add("");
        var pages = lines.Chunk(46).Select(page =>
        {
            var content = new StringBuilder();
            for (var index = 0; index < page.Length; index++)
                AddText(content, page[index], 50, 790 - index * 15, 11);
            return content;
        }).ToList();
        return BuildDocument(pages, lines);
    }

    /// <summary>Column widths are positive relative weights, fitted to the printable A4 width.</summary>
    public static byte[] BuildReport(string title, IEnumerable<string> introduction,
        IEnumerable<EvaluationPdfTable> tables, IEnumerable<string>? closing = null)
    {
        var font = PdfFont.Load();
        var layout = new ReportLayout(title, font);
        layout.AddParagraphs(introduction);
        foreach (var table in tables) layout.AddTable(table);
        if (closing is not null) layout.AddParagraphs(closing);
        return BuildDocument(layout.Pages, layout.Text, font);
    }

    private sealed class ReportLayout
    {
        private const double Left = 50, Width = 495, Bottom = 65;
        private const double FontSize = 9, LineHeight = 13, Padding = 5;
        private readonly List<string> _title;
        private readonly PdfFont _font;
        private double _y;
        public List<StringBuilder> Pages { get; } = [];
        public HashSet<string> Text { get; } = [];
        private StringBuilder Page => Pages[^1];

        public ReportLayout(string title, PdfFont font)
        {
            _font = font;
            _title = Wrap(title, Width / 14, font).ToList();
            if (_title.Count > 6) throw new ArgumentException("PDF 报告标题过长。", nameof(title));
            NewPage();
        }

        private void NewPage()
        {
            Pages.Add(new StringBuilder());
            _y = 794;
            foreach (var line in _title)
            {
                TextAt(line, Left, _y - 14, 14);
                _y -= 19;
            }
            Page.Append(FormattableString.Invariant($"0.65 G {Left} {_y - 4} m {Left + Width} {_y - 4} l S 0 G\n"));
            _y -= 19;
        }

        public void AddParagraphs(IEnumerable<string> paragraphs)
        {
            foreach (var paragraph in paragraphs)
            {
                foreach (var line in Wrap(paragraph, Width / 10, _font))
                {
                    if (_y - 15 < Bottom) NewPage();
                    TextAt(line, Left, _y - 10, 10);
                    _y -= 15;
                }
                _y -= 5;
            }
        }

        public void AddTable(EvaluationPdfTable table)
        {
            ArgumentNullException.ThrowIfNull(table);
            if (table.Headers.Count is < 1 or > 12)
                throw new ArgumentException("PDF 表格需包含 1 至 12 列。");
            if (table.Rows.Any(row => row.Count != table.Headers.Count))
                throw new ArgumentException("PDF 表格数据列数与表头不一致。");
            var relative = table.ColumnWidths?.ToArray() ?? Enumerable.Repeat(1d, table.Headers.Count).ToArray();
            if (relative.Length != table.Headers.Count || relative.Any(value => !double.IsFinite(value) || value <= 0)
                || !double.IsFinite(relative.Sum())) throw new ArgumentException("PDF 表格列宽无效。");
            var widths = relative.Select(value => Width * value / relative.Sum()).ToArray();
            if (widths.Any(width => width < FontSize * 2 + Padding * 2))
                throw new ArgumentException("PDF 表格列宽过窄，至少需容纳两个字符。");
            var headers = WrapCells(table.Headers, widths);
            var headerHeight = headers.Max(lines => lines.Count) * LineHeight + Padding * 2;
            var titleLines = Wrap(table.Title, Width / 11, _font).ToList();
            var headingHeight = titleLines.Count * 15 + 8 + headerHeight;
            // Leave room for at least one data line; impossible headings must not cause a pagination loop.
            if (headingHeight > 350) throw new ArgumentException("PDF 表格标题或表头过长。");

            void Heading(bool continued)
            {
                foreach (var line in titleLines)
                {
                    TextAt(line, Left, _y - 11, 11);
                    _y -= 15;
                }
                if (continued) TextAt("续表", Left + Width - 22, _y - 7, 7);
                _y -= 8;
                DrawRow(headers, widths, 0, headers.Max(lines => lines.Count), header: true, shade: false);
            }

            if (_y - headingHeight - LineHeight - Padding * 2 < Bottom) NewPage();
            Heading(false);
            for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
            {
                var cells = WrapCells(table.Rows[rowIndex], widths);
                var lineCount = cells.Max(lines => lines.Count);
                var offset = 0;
                // Keep normal rows together when they fit on a fresh page; split genuinely long rows.
                var freshSpace = 794 - _title.Count * 19 - 19 - headingHeight - Bottom;
                if (lineCount * LineHeight + Padding * 2 <= freshSpace
                    && _y - lineCount * LineHeight - Padding * 2 < Bottom)
                {
                    NewPage();
                    Heading(true);
                }
                while (offset < lineCount)
                {
                    var available = (int)Math.Floor((_y - Bottom - Padding * 2) / LineHeight);
                    if (available < 1)
                    {
                        NewPage();
                        Heading(true);
                        continue;
                    }
                    var count = Math.Min(available, lineCount - offset);
                    DrawRow(cells, widths, offset, count, header: false, shade: rowIndex % 2 == 1);
                    offset += count;
                }
            }
            _y -= 16;
        }

        private List<string>[] WrapCells(IReadOnlyList<string> cells, double[] widths) =>
            cells.Select((text, index) => Wrap(text, (widths[index] - Padding * 2) / FontSize, _font).ToList()).ToArray();

        private void DrawRow(List<string>[] cells, double[] widths, int offset, int count, bool header, bool shade)
        {
            var height = count * LineHeight + Padding * 2;
            var x = Left;
            for (var column = 0; column < cells.Length; column++)
            {
                Page.Append(FormattableString.Invariant($"q {(header ? 0.9 : shade ? 0.97 : 1):0.##} g {x:0.###} {_y - height:0.###} {widths[column]:0.###} {height:0.###} re f Q\n"));
                Page.Append(FormattableString.Invariant($"0.7 G 0.4 w {x:0.###} {_y - height:0.###} {widths[column]:0.###} {height:0.###} re S 0 G\n"));
                // Repeat a short row identifier alongside evidence continued onto another page.
                var cellOffset = column == 0 && offset >= cells[column].Count && cells[column].Count <= count ? 0 : offset;
                for (var line = 0; line < count && cellOffset + line < cells[column].Count; line++)
                    TextAt(cells[column][cellOffset + line], x + Padding, _y - Padding - FontSize - line * LineHeight, FontSize);
                x += widths[column];
            }
            _y -= height;
        }

        private void TextAt(string text, double x, double y, double size)
        {
            Text.Add(text);
            AddText(Page, text, x, y, size);
        }
    }

    // Wrapping and the PDF /W array use the same embedded font advances.
    private static IEnumerable<string> Wrap(string? line, double ems, PdfFont? font = null)
    {
        foreach (var paragraph in (line ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "  ").Split('\n'))
        {
            if (paragraph.Length == 0) { yield return ""; continue; }
            var part = new StringBuilder();
            double Measure(string value) => value.Sum(character => font?.Width(character) / 1000d ?? 1);
            var used = 0d;
            var breakOffset = 0;
            var elements = StringInfo.GetTextElementEnumerator(paragraph);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                var advance = Measure(element);
                while (part.Length > 0 && used + advance > ems + 0.000001)
                {
                    // Prefer word/path boundaries, but split identifiers longer than a cell.
                    var length = font is not null && breakOffset > 0 ? breakOffset : part.Length;
                    yield return part.ToString(0, length);
                    part.Remove(0, length);
                    used = Measure(part.ToString());
                    breakOffset = 0;
                }
                part.Append(element);
                used += advance;
                if (char.IsWhiteSpace(element[0]) || element[0] >= 0x2e80 || element is "/" or "-" or ";")
                    breakOffset = part.Length;
            }
            if (part.Length > 0) yield return part.ToString();
        }
    }

    private static void AddText(StringBuilder content, string text, double x, double y, double size) =>
        content.Append(FormattableString.Invariant($"BT /F1 {size:0.###} Tf 1 0 0 1 {x:0.###} {y:0.###} Tm <"))
            .Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text))).Append("> Tj ET\n");

    private static byte[] BuildDocument(IReadOnlyList<StringBuilder> pages, IEnumerable<string> sourceLines, PdfFont? loadedFont = null)
    {
        var font = loadedFont ?? PdfFont.Load();
        var characters = sourceLines.Append("0123456789 /续表").SelectMany(line => line).Distinct().Order().ToArray();
        var widths = string.Join(" ", characters.Select(character => $"{(int)character} [{font.Width(character)}]"));
        var glyphs = new byte[65536 * 2];
        foreach (var character in characters)
        {
            var glyph = font.Cmap.GetGlyphId(character);
            glyphs[character * 2] = (byte)(glyph >> 8);
            glyphs[character * 2 + 1] = (byte)glyph;
        }
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii($"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages.Count).Select(index => $"{10 + index * 2} 0 R"))}] /Count {pages.Count} >>"),
            Ascii("<< /Type /Font /Subtype /Type0 /BaseFont /EvaluationChinese /Encoding /Identity-H /DescendantFonts [4 0 R] /ToUnicode 9 0 R >>"),
            Ascii($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /EvaluationChinese /CIDSystemInfo 5 0 R /FontDescriptor 6 0 R /CIDToGIDMap 7 0 R /DW 1000 /W [{widths}] >>"),
            Ascii("<< /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>"),
            Ascii("<< /Type /FontDescriptor /FontName /EvaluationChinese /Flags 4 /FontBBox [0 -220 1000 900] /ItalicAngle 0 /Ascent 880 /Descent -220 /CapHeight 700 /StemV 80 /FontFile2 8 0 R >>"),
            Stream(glyphs), Stream(font.Bytes),
            Stream(Ascii("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /EvaluationUnicode def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n1 beginbfrange\n<0000> <FFFF> <0000>\nendbfrange\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend")),
        };
        for (var index = 0; index < pages.Count; index++)
        {
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {11 + index * 2} 0 R >>"));
            var content = new StringBuilder(pages[index].ToString());
            var footer = $"{index + 1} / {pages.Count}";
            AddText(content, footer, (595 - footer.Sum(character => font.Width(character)) * 9 / 1000d) / 2, 40, 9);
            objects.Add(Stream(Ascii(content.ToString())));
        }
        return Write(objects);
    }

    private sealed class PdfFont
    {
        public byte[] Bytes { get; }
        public TrueTypeCmap Cmap { get; }
        private readonly int _unitsPerEm, _metrics, _metricCount;
        private readonly Dictionary<char, int> _widths = [];

        private PdfFont(byte[] bytes)
        {
            Bytes = bytes;
            Cmap = TrueTypeCmap.Load(bytes);
            var tables = Enumerable.Range(0, U16(4)).ToDictionary(
                index => Encoding.ASCII.GetString(bytes, 12 + index * 16, 4),
                index => checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20 + index * 16, 4))));
            _unitsPerEm = U16(tables["head"] + 18);
            _metrics = tables["hmtx"];
            _metricCount = U16(tables["hhea"] + 34);
            if (_unitsPerEm == 0 || _metricCount == 0) throw new InvalidOperationException("PDF 字体尺寸无效。");
        }

        public int Width(char character)
        {
            if (!_widths.TryGetValue(character, out var width))
                _widths[character] = width = (int)Math.Ceiling(
                    U16(_metrics + Math.Min(Cmap.GetGlyphId(character), _metricCount - 1) * 4) * 1000d / _unitsPerEm);
            return width;
        }

        private ushort U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan(offset, 2));

        public static PdfFont Load()
        {
            var candidates = new[] { Environment.GetEnvironmentVariable("EVALUATION_PDF_FONT"),
                @"C:\Windows\Fonts\simhei.ttf", @"C:\Windows\Fonts\simsunb.ttf", @"C:\Windows\Fonts\Deng.ttf" };
            var path = candidates.FirstOrDefault(candidate => candidate is not null && File.Exists(candidate))
                ?? throw new InvalidOperationException("未找到中文字体，请设置 EVALUATION_PDF_FONT 指向中文 TrueType 字体。");
            return new PdfFont(File.ReadAllBytes(path));
        }
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    private static byte[] Stream(byte[] content)
    {
        using var buffer = new MemoryStream();
        buffer.Write(Ascii($"<< /Length {content.Length} >>\nstream\n"));
        buffer.Write(content);
        buffer.Write(Ascii("\nendstream"));
        return buffer.ToArray();
    }

    private static byte[] Write(IReadOnlyList<byte[]> objects)
    {
        using var buffer = new MemoryStream();
        void Append(string value) => buffer.Write(Ascii(value));
        Append("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(buffer.Position);
            Append($"{index + 1} 0 obj\n");
            buffer.Write(objects[index]);
            Append("\nendobj\n");
        }
        var xrefOffset = buffer.Position;
        Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Append($"{offset:0000000000} 00000 n \n");
        Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return buffer.ToArray();
    }
}
