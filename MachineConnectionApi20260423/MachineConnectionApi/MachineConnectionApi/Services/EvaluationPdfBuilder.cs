using System.Text;

namespace MachineConnectionApi.Services;

public static class EvaluationPdfBuilder
{
    public static byte[] Build(IEnumerable<string> sourceLines)
    {
        var lines = sourceLines.SelectMany(Wrap).ToList();
        if (lines.Count == 0) lines.Add("");
        var candidates = new[] { Environment.GetEnvironmentVariable("EVALUATION_PDF_FONT"),
            @"C:\Windows\Fonts\simhei.ttf", @"C:\Windows\Fonts\simsunb.ttf", @"C:\Windows\Fonts\Deng.ttf" };
        var fontPath = candidates.FirstOrDefault(path => path is not null && File.Exists(path))
            ?? throw new InvalidOperationException("未找到中文字体，请设置 EVALUATION_PDF_FONT 指向中文 TrueType 字体。");
        var font = File.ReadAllBytes(fontPath!);
        var cmap = TrueTypeCmap.Load(font);
        var glyphs = new byte[65536 * 2];
        foreach (var character in lines.SelectMany(line => line).Distinct())
        {
            var glyph = cmap.GetGlyphId(character);
            glyphs[character * 2] = (byte)(glyph >> 8);
            glyphs[character * 2 + 1] = (byte)glyph;
        }
        var pages = lines.Chunk(46).ToArray();
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii($"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages.Length).Select(index => $"{10 + index * 2} 0 R"))}] /Count {pages.Length} >>"),
            Ascii("<< /Type /Font /Subtype /Type0 /BaseFont /EvaluationChinese /Encoding /Identity-H /DescendantFonts [4 0 R] /ToUnicode 9 0 R >>"),
            Ascii("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /EvaluationChinese /CIDSystemInfo 5 0 R /FontDescriptor 6 0 R /CIDToGIDMap 7 0 R /DW 1000 >>"),
            Ascii("<< /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>"),
            Ascii("<< /Type /FontDescriptor /FontName /EvaluationChinese /Flags 4 /FontBBox [0 -220 1000 900] /ItalicAngle 0 /Ascent 880 /Descent -220 /CapHeight 700 /StemV 80 /FontFile2 8 0 R >>"),
            Stream(glyphs), Stream(font),
            Stream(Ascii("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /EvaluationUnicode def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n1 beginbfrange\n<0000> <FFFF> <0000>\nendbfrange\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend")),
        };
        for (var index = 0; index < pages.Length; index++)
        {
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {11 + index * 2} 0 R >>"));
            var content = new StringBuilder("BT\n/F1 11 Tf\n15 TL\n50 790 Td\n");
            foreach (var line in pages[index])
                content.Append('<').Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(line))).Append("> Tj\nT*\n");
            content.Append("ET\nBT /F1 9 Tf 260 40 Td <")
                .Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes($"{index + 1} / {pages.Length}"))).Append("> Tj ET");
            objects.Add(Stream(Ascii(content.ToString())));
        }
        return Write(objects);
    }

    private static IEnumerable<string> Wrap(string line)
    {
        foreach (var paragraph in (line ?? "").Replace("\r", "").Replace("\t", "  ").Split('\n'))
        {
            if (paragraph.Length == 0) { yield return ""; continue; }
            for (var offset = 0; offset < paragraph.Length; offset += 44)
                yield return paragraph.Substring(offset, Math.Min(44, paragraph.Length - offset));
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
