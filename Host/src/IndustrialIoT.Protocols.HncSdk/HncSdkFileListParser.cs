namespace IndustrialIoT.Protocols.HncSdk;

public static class HncSdkFileListParser
{
    public static IReadOnlyList<HncSdkFileEntry> Parse(string? parentPath, IEnumerable<string> lines)
    {
        var entries = new List<HncSdkFileEntry>();
        foreach (var raw in lines)
        {
            var entry = ParseLine(parentPath, raw);
            if (entry is not null) entries.Add(entry);
        }
        return entries;
    }

    public static IReadOnlyList<HncSdkFileEntry> Parse(string? parentPath, string content)
        => Parse(parentPath, content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static HncSdkFileEntry? ParseLine(string? parentPath, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var isDirectory = line[0] == 'd';
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // ls -la layout: <perm> <links> <user> <group> <size> <Mon> <Day> <Time/Year> <name…>
        // 只在明确符合该形态时才提取 size 与"靠后字段才是名字"，避免把月份/分钟当成 size。
        var lsShape = tokens.Length >= 9
            && (line[0] == 'd' || line[0] == '-' || line[0] == 'l')
            && long.TryParse(tokens[4], out _);

        string name;
        long? size = null;
        if (lsShape)
        {
            size = long.Parse(tokens[4]);
            // 名字从第 9 个字段开始（索引 8），含其后所有空格；用第 8 个 token 在原行的位置切。
            var nameStart = IndexOfNthToken(line, 8);
            name = nameStart >= 0 ? line[nameStart..].Trim() : tokens[^1];
        }
        else
        {
            // 退化路径：按"最后一个空格之后是名字"（与官方 demo SplitRemoteInfo 同策略）。
            var idx = line.LastIndexOf(' ');
            name = idx < 0 ? line.Trim() : line[(idx + 1)..].Trim();
        }

        if (name.Length == 0 || name == "." || name == "..") return null;

        var normalizedParent = NormalizeParent(parentPath);
        var path = normalizedParent + name + (isDirectory ? "/" : "");
        return new HncSdkFileEntry(path, name, isDirectory, size);
    }

    private static int IndexOfNthToken(string line, int tokenIndex)
    {
        var seen = 0;
        var inToken = false;
        for (var i = 0; i < line.Length; i++)
        {
            var space = line[i] == ' ';
            if (!space && !inToken)
            {
                if (seen == tokenIndex) return i;
                inToken = true;
                seen++;
            }
            else if (space)
            {
                inToken = false;
            }
        }
        return -1;
    }

    private static string NormalizeParent(string? parentPath)
    {
        if (string.IsNullOrWhiteSpace(parentPath) || parentPath == "/") return "";
        return parentPath.TrimStart('/').TrimEnd('/') + "/";
    }
}
