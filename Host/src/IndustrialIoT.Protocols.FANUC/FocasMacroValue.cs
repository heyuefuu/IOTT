namespace IndustrialIoT.Protocols.FANUC;

using System.Globalization;

/// <summary>FOCAS default decimal macro format: M * 10^(-E), with nine significant digits.</summary>
internal static class FocasMacroValue
{
    private const int MaximumMantissa = 999999999;
    private const int MinimumDecimals = -128;
    private const int MaximumDecimals = 127;

    public static bool IsValidNumber(int number) => number is >= 1 and <= short.MaxValue;

    public static bool TryEncode(double value, out int mantissa, out short decimals)
    {
        mantissa = 0;
        decimals = 0;
        if (!double.IsFinite(value))
            return false;
        if (value == 0)
            return true;

        // Round-trip text preserves the caller's value; never round excess precision away.
        var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponentIndex = text.IndexOf('E');
        var exponent = exponentIndex < 0 ? 0
            : int.Parse(text.AsSpan(exponentIndex + 1), CultureInfo.InvariantCulture);
        var coefficient = exponentIndex < 0 ? text : text[..exponentIndex];
        var pointIndex = coefficient.IndexOf('.');
        var decimalPlaces = (pointIndex < 0 ? 0 : coefficient.Length - pointIndex - 1) - exponent;
        var digits = coefficient.Replace(".", string.Empty).TrimStart('0');
        var significantDigits = digits.TrimEnd('0');
        decimalPlaces -= digits.Length - significantDigits.Length;
        if (decimalPlaces > MaximumDecimals || significantDigits.Length > 9)
            return false;

        if (decimalPlaces < MinimumDecimals)
        {
            var padding = MinimumDecimals - decimalPlaces;
            if (significantDigits.Length + padding > 9)
                return false;
            significantDigits += new string('0', padding);
            decimalPlaces = MinimumDecimals;
        }

        mantissa = int.Parse(significantDigits, CultureInfo.InvariantCulture);
        if (value < 0)
            mantissa = -mantissa;
        decimals = (short)decimalPlaces;
        return true;
    }

    public static bool TryDecode(int mantissa, short decimals, out double value)
    {
        value = double.NaN;
        // (0, -1) is the SDK's vacant-variable marker, not numeric zero.
        if ((mantissa == 0 && decimals == -1)
            || mantissa is < -MaximumMantissa or > MaximumMantissa
            || decimals is < MinimumDecimals or > MaximumDecimals)
            return false;

        value = double.Parse(FormattableString.Invariant($"{mantissa}E{-decimals}"),
            CultureInfo.InvariantCulture);
        return true;
    }
}
