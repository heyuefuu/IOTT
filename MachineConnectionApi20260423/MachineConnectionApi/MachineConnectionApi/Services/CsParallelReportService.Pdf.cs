using System.Globalization;
using System.Text;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

public sealed partial class CsParallelReportService
{
    private static readonly string[] ChineseFontCandidates =
    {
        @"C:\Windows\Fonts\simhei.ttf",
        @"C:\Windows\Fonts\simsunb.ttf",
        @"C:\Windows\Fonts\Deng.ttf"
    };

    private static byte[] BuildPdf(CsParallelReportRequest report)
    {
        var lines = BuildPdfLines(report).Take(44).ToList();
        var fontBytes = LoadChineseFont();
        var cmap = TrueTypeCmap.Load(fontBytes);
        var cidToGid = BuildCidToGidMap(lines, cmap);
        var streamText = BuildContentStream(lines);
        var contentBytes = Encoding.ASCII.GetBytes(streamText);
        var objects = new List<byte[]>
        {
            A("<< /Type /Catalog /Pages 2 0 R >>"),
            A("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            A("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 8 0 R >>"),
            A("<< /Type /Font /Subtype /Type0 /BaseFont /EmbeddedChinese /Encoding /Identity-H /DescendantFonts [5 0 R] >>"),
            A("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /EmbeddedChinese /CIDSystemInfo 6 0 R /FontDescriptor 7 0 R /CIDToGIDMap 9 0 R /DW 1000 >>"),
            A("<< /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>"),
            A($"<< /Type /FontDescriptor /FontName /EmbeddedChinese /Flags 4 /FontBBox [0 -220 1000 900] /ItalicAngle 0 /Ascent 880 /Descent -220 /CapHeight 700 /StemV 80 /FontFile2 10 0 R >>"),
            Stream(contentBytes),
            Stream(cidToGid),
            Stream(fontBytes)
        };
        return WritePdf(objects);
    }

    private static string BuildContentStream(IReadOnlyList<string> lines)
    {
        var stream = new StringBuilder("BT\n/F1 12 Tf\n14 TL\n50 790 Td\n");
        foreach (var line in lines)
            stream.Append('<').Append(ToUtf16Hex(line)).Append("> Tj\nT*\n");
        stream.Append("ET");
        return stream.ToString();
    }

    private static IEnumerable<string> BuildPdfLines(CsParallelReportRequest report)
    {
        yield return "并行连接验证报告";
        yield return $"协议: {Text(report.Request.Protocol)}";
        yield return $"目标地址: {Text(report.Request.StartIp)}:{report.Request.Port}";
        yield return $"模拟设备数量: {report.Request.DeviceCount}";
        yield return $"并发连接数: {report.Request.ConcurrentCount}";
        yield return $"连接模式: {ModeText(report)}";
        yield return $"测试时长: {(report.DurationSeconds > 0 ? report.DurationSeconds.ToString(CultureInfo.InvariantCulture) : "-")} 秒";
        yield return $"超时时间: {report.Request.TimeoutMs} ms";
        yield return $"生成时间: {Text(report.GeneratedAt)}";
        yield return $"完成时间: {Text(report.Result.FinishedAt)}";
        yield return $"总连接数: {report.Result.Total}";
        yield return $"成功数: {report.Result.Success}";
        yield return $"失败数: {report.Result.Failure}";
        yield return $"成功率: {report.Result.SuccessRate}%";
        yield return $"平均响应时间: {report.Result.AvgRttMs:0.##} ms";
        yield return $"最大响应时间: {report.Result.MaxRttMs:0.##} ms";
        yield return "失败明细:";
        if (report.Result.Failures.Count == 0)
        {
            yield return "无失败记录";
            yield break;
        }
        foreach (var failure in report.Result.Failures)
            yield return $"{Text(failure.Time)} {Text(failure.DeviceIp)} {Text(failure.Error)}";
    }

    private static byte[] BuildCidToGidMap(IEnumerable<string> lines, TrueTypeCmap cmap)
    {
        var chars = lines.SelectMany(line => line).Distinct().ToArray();
        var max = chars.Select(ch => (int)ch).DefaultIfEmpty(0).Max();
        var map = new byte[(max + 1) * 2];
        foreach (var ch in chars)
        {
            var glyph = cmap.GetGlyphId(ch);
            map[ch * 2] = (byte)(glyph >> 8);
            map[ch * 2 + 1] = (byte)(glyph & 0xFF);
        }
        return map;
    }

    private static byte[] LoadChineseFont()
    {
        var path = ChineseFontCandidates.FirstOrDefault(File.Exists);
        if (path is null)
            throw new InvalidOperationException("未找到可嵌入的中文字体，请确认 Windows Fonts 目录包含 simhei.ttf、simsunb.ttf 或 Deng.ttf");
        return File.ReadAllBytes(path);
    }

    private static byte[] WritePdf(IReadOnlyList<byte[]> objects)
    {
        using var ms = new MemoryStream();
        void Write(string value) => ms.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(ms.Position);
            Write($"{index + 1} 0 obj\n");
            ms.Write(objects[index]);
            Write("\nendobj\n");
        }
        var xrefOffset = ms.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
            Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] Stream(byte[] content)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes($"<< /Length {content.Length} >>\nstream\n"));
        ms.Write(content);
        ms.Write(Encoding.ASCII.GetBytes("\nendstream"));
        return ms.ToArray();
    }

    private static byte[] A(string value) => Encoding.ASCII.GetBytes(value);

    private static string ToUtf16Hex(string value)
    {
        var bytes = Encoding.BigEndianUnicode.GetBytes(value);
        return Convert.ToHexString(bytes);
    }
}