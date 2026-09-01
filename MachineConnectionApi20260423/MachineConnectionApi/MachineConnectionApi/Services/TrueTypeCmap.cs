using System.Buffers.Binary;

namespace MachineConnectionApi.Services;

internal sealed class TrueTypeCmap
{
    private readonly byte[] _font;
    private readonly int _format;
    private readonly int _subtable;
    private readonly int _segCount;
    private readonly int _groupsOffset;
    private readonly int _groupCount;

    private TrueTypeCmap(byte[] font, int subtable)
    {
        _font = font;
        _subtable = subtable;
        _format = U16(subtable);
        if (_format == 4)
            _segCount = U16(subtable + 6) / 2;
        else if (_format == 12)
        {
            _groupsOffset = subtable + 16;
            _groupCount = (int)U32(subtable + 12);
        }
        else
            throw new InvalidOperationException($"Unsupported cmap format {_format}");
    }

    public static TrueTypeCmap Load(byte[] font)
    {
        var cmapOffset = TableOffset(font, "cmap");
        var tableCount = ReadU16(font, cmapOffset + 2);
        var bestOffset = -1;
        var bestScore = -1;
        for (var i = 0; i < tableCount; i++)
        {
            var record = cmapOffset + 4 + i * 8;
            var platform = ReadU16(font, record);
            var encoding = ReadU16(font, record + 2);
            var offset = cmapOffset + (int)ReadU32(font, record + 4);
            var format = ReadU16(font, offset);
            var score = Score(platform, encoding, format);
            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = offset;
            }
        }
        if (bestOffset < 0)
            throw new InvalidOperationException("Font cmap table has no Unicode subtable");
        return new TrueTypeCmap(font, bestOffset);
    }

    public ushort GetGlyphId(char ch)
    {
        var code = ch;
        return _format switch
        {
            4 => GetGlyphIdFormat4(code),
            12 => GetGlyphIdFormat12(code),
            _ => 0
        };
    }

    private ushort GetGlyphIdFormat4(int code)
    {
        var endCodes = _subtable + 14;
        var startCodes = endCodes + _segCount * 2 + 2;
        var idDeltas = startCodes + _segCount * 2;
        var idRangeOffsets = idDeltas + _segCount * 2;
        for (var i = 0; i < _segCount; i++)
        {
            var end = U16(endCodes + i * 2);
            if (code > end) continue;
            var start = U16(startCodes + i * 2);
            if (code < start) return 0;
            var delta = (short)U16(idDeltas + i * 2);
            var rangeOffsetPos = idRangeOffsets + i * 2;
            var rangeOffset = U16(rangeOffsetPos);
            if (rangeOffset == 0)
                return (ushort)((code + delta) & 0xFFFF);
            var glyphPos = rangeOffsetPos + rangeOffset + (code - start) * 2;
            if (glyphPos + 1 >= _font.Length) return 0;
            var glyph = U16(glyphPos);
            return glyph == 0 ? (ushort)0 : (ushort)((glyph + delta) & 0xFFFF);
        }
        return 0;
    }

    private ushort GetGlyphIdFormat12(int code)
    {
        for (var i = 0; i < _groupCount; i++)
        {
            var group = _groupsOffset + i * 12;
            var start = U32(group);
            var end = U32(group + 4);
            if (code < start) return 0;
            if (code > end) continue;
            return (ushort)(U32(group + 8) + code - start);
        }
        return 0;
    }

    private ushort U16(int offset) => ReadU16(_font, offset);
    private uint U32(int offset) => ReadU32(_font, offset);

    private static int TableOffset(byte[] font, string tag)
    {
        var tableCount = ReadU16(font, 4);
        for (var i = 0; i < tableCount; i++)
        {
            var record = 12 + i * 16;
            var current = System.Text.Encoding.ASCII.GetString(font, record, 4);
            if (current == tag)
                return (int)ReadU32(font, record + 8);
        }
        throw new InvalidOperationException($"Font table {tag} not found");
    }

    private static int Score(int platform, int encoding, int format) => (platform, encoding, format) switch
    {
        (3, 10, 12) => 60,
        (0, _, 12) => 50,
        (3, 1, 4) => 40,
        (0, _, 4) => 30,
        _ => -1
    };

    private static ushort ReadU16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));

    private static uint ReadU32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
}