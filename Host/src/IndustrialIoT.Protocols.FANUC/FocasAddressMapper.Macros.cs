namespace IndustrialIoT.Protocols.FANUC;

using System.Globalization;
using System.Text.RegularExpressions;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

internal sealed partial class FocasAddressMapper
{
    private static bool TryGetMacroNumber(string path, out int number)
    {
        number = 0;
        var match = MacroRegex().Match(path);
        if (!match.Success)
            return false;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number)
            || !FocasMacroValue.IsValidNumber(number))
            throw new ArgumentOutOfRangeException(nameof(path), "FOCAS macro number must be 1-32767.");
        return true;
    }

    private static object ConvertMacroScalar(object value, DataType dataType)
    {
        if (dataType is not (DataType.Int8 or DataType.UInt8 or DataType.Int16 or DataType.UInt16
            or DataType.Int32 or DataType.UInt32 or DataType.Int64 or DataType.UInt64
            or DataType.Float or DataType.Double))
            throw new ArgumentException("CNC macros support integer and floating-point types; use PMC outputs for Bool.");

        var converted = TagValueConversion.ConvertScalar(value, dataType);
        if (converted is float single && single == 0
            && (double)TagValueConversion.ConvertScalar(value, DataType.Double) != 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Macro value underflows Float precision.");
        return converted;
    }

    private void WriteMacro(int number, DataType dataType, object value)
    {
        // Only common variables are exposed for writes; system/local variables remain read-only.
        if (number is not (>= 100 and <= 199 or >= 500 and <= 999))
            throw new InvalidOperationException("Only common CNC macros #100-199 and #500-999 support writes.");

        var converted = ConvertMacroScalar(value, dataType);
        var culture = CultureInfo.InvariantCulture;
        if (dataType is DataType.Int64 or DataType.UInt64
            && Math.Abs(Convert.ToDecimal(converted, culture)) > 9007199254740991m)
            throw new ArgumentOutOfRangeException(nameof(value), "Macro integer exceeds the exact Double integer range.");

        // Preserve the declared Float's decimal value instead of widening its binary rounding noise.
        var numeric = converted is float single
            ? double.Parse(single.ToString("R", culture), culture)
            : Convert.ToDouble(converted, culture);
        if (!FocasMacroValue.TryEncode(numeric, out _, out _))
            throw new ArgumentOutOfRangeException(nameof(value),
                "Macro value exceeds FOCAS decimal range or nine-significant-digit precision.");

        ThrowIfError(_api.WriteMacroVariable(_handle, number, numeric), "WriteMacroVariable");
    }

    private static IReadOnlyList<AddressNode> MakeMacroNodes() =>
    [
        MakeVar("/CNC/Macro/500", "Common macro #500 (example)", DataType.Double, true, true),
        MakeVar("/CNC/Macro/501", "Common macro #501 (example)", DataType.Double, true, true),
    ];

    [GeneratedRegex(@"^/(?:cnc/macro/|macro:)([0-9]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex MacroRegex();
}
