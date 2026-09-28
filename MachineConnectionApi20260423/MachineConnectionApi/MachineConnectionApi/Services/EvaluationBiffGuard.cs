using System.Buffers.Binary;
using System.Text;

namespace MachineConnectionApi.Services;

// A bounded import preflight, not a cell/formula evaluator. ExcelDataReader's public API exposes
// cached formula values but not formula provenance. Inspect BIFF record headers in the exact
// Workbook/Book stream subsequently passed to that reader; never search arbitrary file bytes.
internal static class EvaluationBiffGuard
{
    internal const string FormulaMessage = "导入单元格应填写实际数值，不接受公式。";
    private const string InvalidMessage = "XLS 文件损坏或工作簿结构无效，请另存为未加密的 XLS/XLSX 后重试。";
    private const string UnsupportedMessage = "无法安全检查此 XLS 变种；仅支持 BIFF5/BIFF8 工作簿，请另存为未加密的 XLS/XLSX 后重试。";
    private const uint Free = 0xffffffff;
    private const uint End = 0xfffffffe;
    private const uint FatSector = 0xfffffffd;
    private const int MaxSubstreams = 256;

    internal static MemoryStream OpenValuesOnlyWorkbook(MemoryStream input)
    {
        var file = input.GetBuffer().AsSpan(0, checked((int)input.Length));
        var bytes = file.Length >= 8 && file[0] == 0xd0 ? ExtractWorkbook(file) : file.ToArray();
        ValidateRecords(bytes);
        return new MemoryStream(bytes, writable: false);
    }

    private static void ValidateRecords(ReadOnlySpan<byte> data)
    {
        var depth = 0;
        var version = 0;
        var substreamCount = 0;
        var sheetOffsets = new HashSet<int>();
        var actualSheetOffsets = new HashSet<int>();
        for (var offset = 0; offset < data.Length;)
        {
            // Some writers (including xlwt) round the Workbook stream up to 4096 bytes.
            // Only all-zero padding OUTSIDE a completed substream is allowed.
            if (depth == 0 && offset != 0 && data[offset..].IndexOfAnyExcept((byte)0) < 0) break;
            Require(data.Length - offset >= 4);
            var id = U16(data, offset);
            var length = U16(data, offset + 2);
            Require(length <= 8224 && length <= data.Length - offset - 4);
            var payload = data.Slice(offset + 4, length);
            if (id is 0x0009 or 0x0209 or 0x0409) throw new ArgumentException(UnsupportedMessage);
            if (id == 0x0809) // BOF: BIFF5 and BIFF8 use the same record ID.
            {
                Require(length >= 8);
                var recordVersion = U16(payload, 0);
                if (recordVersion is not (0x0500 or 0x0600)) throw new ArgumentException(UnsupportedMessage);
                if (offset == 0)
                {
                    version = recordVersion;
                    Require(U16(payload, 2) == 0x0005); // Workbook globals, not older worksheet-only formats.
                }
                else
                {
                    Require(version == recordVersion && U16(payload, 2) != 0x0005);
                    if (depth == 0) actualSheetOffsets.Add(offset);
                }
                Require(++substreamCount <= MaxSubstreams && ++depth <= 16);
            }
            else
            {
                Require(depth > 0);
                switch (id)
                {
                    // FORMULA (including older IDs), ARRAY, SHRFMLA, data TABLE, and STRING
                    // (a formula's string cache, never a literal LABEL/SST). Reject every cache type.
                    case 0x0006: case 0x0206: case 0x0406:
                    case 0x0021: case 0x0221: case 0x04bc:
                    case 0x0036: case 0x0236: case 0x0007: case 0x0207:
                        throw new ArgumentException(FormulaMessage);
                    case 0x002f: // FILEPASS: do not let the library open default-password encryption.
                        throw new ArgumentException("不支持加密或需要密码的 XLS 文件，请在 Excel 中解除加密后重新导入。");
                    case 0x0085: // BOUNDSHEET offsets must point to scanned BOFs, not bytes inside another record.
                        Require(substreamCount == 1 && depth == 1 && length >= (version == 0x0600 ? 8 : 7));
                        var sheetOffset = U32(payload, 0);
                        Require(sheetOffset < data.Length && sheetOffsets.Add((int)sheetOffset) && sheetOffsets.Count < MaxSubstreams);
                        break;
                    case 0x000a:
                        Require(length == 0);
                        depth--;
                        break;
                }
            }
            offset += 4 + length;
        }
        Require(depth == 0 && version != 0 && sheetOffsets.Count > 0 && sheetOffsets.SetEquals(actualSheetOffsets));
    }

    private static byte[] ExtractWorkbook(ReadOnlySpan<byte> file)
    {
        // MS-CFB v3/v4, regular FAT and MiniFAT. Within our 5MiB input budget an ordinary
        // file needs fewer than the header's 109 FAT slots. Noncanonical external DIFAT is
        // explicitly unsupported instead of implementing another unbounded container reader.
        Require(file.Length >= 512 && U16(file, 28) == 0xfffe);
        var major = U16(file, 26);
        var shift = U16(file, 30);
        Require((major == 3 && shift == 9 || major == 4 && shift == 12) && U16(file, 32) == 6 && U32(file, 56) == 4096);
        if (U32(file, 72) != 0 || U32(file, 68) != End) throw new ArgumentException(UnsupportedMessage);
        var sectorSize = 1 << shift;
        Require(file.Length % sectorSize == 0);
        var sectors = file.Length / sectorSize - 1;
        var fatCount = U32(file, 44);
        Require(fatCount > 0 && fatCount <= 109 && fatCount <= sectors);
        var used = new HashSet<uint>();
        var fat = new uint[sectors];
        var entriesPerSector = sectorSize / 4;
        Require((long)fatCount * entriesPerSector >= sectors);
        for (var index = 0; index < fatCount; index++)
        {
            var sector = U32(file, 76 + index * 4);
            Require(sector < sectors && used.Add(sector));
            var block = file.Slice(((int)sector + 1) * sectorSize, sectorSize);
            for (var entry = 0; entry < entriesPerSector && index * entriesPerSector + entry < sectors; entry++)
                fat[index * entriesPerSector + entry] = U32(block, entry * 4);
        }
        for (var index = (int)fatCount; index < 109; index++) Require(U32(file, 76 + index * 4) == Free);
        foreach (var sector in used) Require(fat[sector] == FatSector);

        var directory = ReadSectors(file, sectorSize, fat, U32(file, 48), null, used);
        Require(directory.Length >= 128 && directory[66] == 5);
        Require(major == 3 ? U32(file, 40) == 0 : U32(file, 40) == directory.Length / sectorSize);
        var workbookEntry = FindWorkbook(directory);
        var workbook = directory.AsSpan(workbookEntry * 128, 128);
        var size = StreamSize(workbook, file.Length);
        Require(size > 0);
        if (size >= 4096) return ReadSectors(file, sectorSize, fat, U32(workbook, 116), size, used);

        var miniFatCount = U32(file, 64);
        Require(miniFatCount > 0 && miniFatCount <= sectors);
        var miniFatBytes = ReadSectors(file, sectorSize, fat, U32(file, 60), checked((int)miniFatCount * sectorSize), used);
        var root = directory.AsSpan(0, 128);
        var miniStream = ReadSectors(file, sectorSize, fat, U32(root, 116), StreamSize(root, file.Length), used);
        Require(miniStream.Length % 64 == 0);
        var miniSectorCount = miniStream.Length / 64;
        Require(miniSectorCount <= miniFatBytes.Length / 4);
        var result = new byte[size];
        var current = U32(workbook, 116);
        var visited = new HashSet<uint>();
        for (var offset = 0; offset < size; offset += 64)
        {
            Require(current < miniSectorCount && visited.Add(current));
            miniStream.AsSpan((int)current * 64, Math.Min(64, size - offset)).CopyTo(result.AsSpan(offset));
            current = U32(miniFatBytes, (int)current * 4);
        }
        Require(current == End);
        return result;
    }

    private static int FindWorkbook(byte[] directory)
    {
        var count = directory.Length / 128;
        var pending = new Stack<uint>();
        pending.Push(U32(directory, 76)); // Root's child; descend siblings only, never embedded storages.
        var visited = new HashSet<uint>();
        var found = -1;
        while (pending.Count > 0)
        {
            var entryId = pending.Pop();
            if (entryId == Free) continue;
            Require(entryId > 0 && entryId < count && visited.Add(entryId));
            var entry = directory.AsSpan((int)entryId * 128, 128);
            Require(entry[66] is 1 or 2);
            var nameLength = U16(entry, 64);
            Require(nameLength is >= 2 and <= 64 && nameLength % 2 == 0 && U16(entry, nameLength - 2) == 0);
            var name = Encoding.Unicode.GetString(entry[..(nameLength - 2)]);
            if (name.Equals("Workbook", StringComparison.OrdinalIgnoreCase) || name.Equals("Book", StringComparison.OrdinalIgnoreCase))
            {
                Require(entry[66] == 2 && found == -1); // No ambiguous Book/Workbook aliases.
                found = (int)entryId;
            }
            pending.Push(U32(entry, 68));
            pending.Push(U32(entry, 72));
        }
        Require(found >= 0);
        return found;
    }

    private static byte[] ReadSectors(ReadOnlySpan<byte> file, int sectorSize, uint[] fat, uint first, int? size, HashSet<uint> used)
    {
        Require(size is null || size >= 0 && size <= file.Length);
        using var output = new MemoryStream();
        var current = first;
        while (current != End)
        {
            // Shared across metadata and stream chains: bounds, cycles AND overlapping chains.
            Require(current < fat.Length && used.Add(current));
            Require(size is null || output.Length < size);
            var count = size is null ? sectorSize : Math.Min(sectorSize, size.Value - (int)output.Length);
            output.Write(file.Slice(((int)current + 1) * sectorSize, count));
            current = fat[current];
        }
        Require(size is null || output.Length == size);
        return output.ToArray();
    }

    private static int StreamSize(ReadOnlySpan<byte> entry, int inputLength)
    {
        var size = BinaryPrimitives.ReadUInt64LittleEndian(entry[120..]);
        Require(size <= (ulong)inputLength);
        return (int)size;
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static void Require(bool condition) { if (!condition) throw new ArgumentException(InvalidMessage); }
}
