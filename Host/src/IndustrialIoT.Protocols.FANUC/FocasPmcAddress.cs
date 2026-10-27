namespace IndustrialIoT.Protocols.FANUC;

using System.Globalization;
using System.Text.RegularExpressions;

internal sealed partial record FocasPmcAddress(
    char Area, int AreaCode, int Start, int? Bit, int? Length, bool IsLegacy = false)
{
    internal const string Areas = "GFYXARTKCDE";
    internal bool IsReadOnly => IsLegacy || Area is 'F' or 'X';

    internal static bool TryParse(string path, out FocasPmcAddress address)
    {
        address = null!;
        const string prefix = "/cnc/pmc/";
        var body = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? path[prefix.Length..] : path.TrimStart('/');
        var legacy = LegacyRegex().Match(body);
        if (legacy.Success)
        {
            var name = legacy.Groups[1].Value.ToUpperInvariant();
            var index = ParseNumber(legacy.Groups[2].Value);
            if (index > (name is "DI" or "DO" ? 15 : 7))
                throw new ArgumentOutOfRangeException(nameof(path), "Legacy PMC bit index is out of range.");
            var area = name switch { "DI" => 'G', "DO" => 'F', "RI" => 'R', _ => 'T' };
            address = new(area, GetAreaCode(area), index / 8, index % 8, null, IsLegacy: true);
            return true;
        }

        var match = AddressRegex().Match(body);
        if (!match.Success)
            return false;
        var areaName = char.ToUpperInvariant(match.Groups[1].Value[0]);
        var start = ParseNumber(match.Groups[2].Value);
        int? bit = match.Groups[3].Success ? ParseNumber(match.Groups[3].Value) : null;
        int? length = match.Groups[4].Success ? ParseNumber(match.Groups[4].Value) : null;
        if (start > ushort.MaxValue || bit is > 7 || length is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(path),
                "PMC byte address must be 0-65535, bit 0-7 and length 1-256.");
        if (bit.HasValue && length.HasValue)
            throw new ArgumentException("A PMC bit address cannot include a byte-array length.");
        address = new(areaName, GetAreaCode(areaName), start, bit, length);
        return true;
    }

    internal static int GetAreaCode(char area) => area switch
    {
        'G' => 0, 'F' => 1, 'Y' => 2, 'X' => 3, 'A' => 4, 'R' => 5,
        'T' => 6, 'K' => 7, 'C' => 8, 'D' => 9, 'E' => 12,
        _ => throw new ArgumentException($"Unknown PMC area '{area}'.")
    };

    private static int ParseNumber(string text)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw new ArgumentOutOfRangeException(nameof(text), "PMC address component is out of range.");
        return number;
    }

    [GeneratedRegex(@"^([GFYXARTKCDE])([0-9]+)(?:\.([0-9]+))?(?:;length=([0-9]+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex AddressRegex();

    [GeneratedRegex(@"^(DI|DO|RI|RO)\[([0-9]+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyRegex();
}
