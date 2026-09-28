namespace IndustrialIoT.Protocols.JingDiao;

using System.Globalization;
using System.Text.Json;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

public sealed partial class JingDiaoDriver
{
    public async Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
    {
        try
        {
            EnsureConnected();
            var parts = Normalize(address).Split([':', '.']);
            if (parts.Length != 2 || !parts[0].Equals("macro", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                throw new ArgumentException("Only Macro:{nonnegative integer} addresses support writes.");
            var numeric = ConvertMacroValue(dataType, value);
            var result = await client!.SetMacroAsync(new(sessionId, number, numeric), ct);
            return new WriteResult { Success = result.ReturnCode == 0, ErrorMessage = result.ReturnCode == 0 ? null : Error(result) };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { return new WriteResult { Success = false, ErrorMessage = error.Message }; }
    }

    private static double ConvertMacroValue(DataType dataType, object value)
    {
        if (value is JsonElement json)
            value = json.ValueKind == JsonValueKind.String ? json.GetString()! : json.GetRawText();
        if (value is null) throw new ArgumentException("Macro value is required.");
        if (dataType == DataType.Bool)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (bool.TryParse(text, out var boolean)) return boolean ? 1 : 0;
            if (text == "0" || text == "1") return text == "1" ? 1 : 0;
            throw new ArgumentException("Boolean macros require true, false, 0 or 1.");
        }
        if (dataType is DataType.Float or DataType.Double)
        {
            var floating = dataType == DataType.Float
                ? Convert.ToSingle(value, CultureInfo.InvariantCulture)
                : Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (!double.IsFinite(floating)) throw new ArgumentException("Macro value must be finite.");
            return floating;
        }
        var bounds = dataType switch
        {
            DataType.Int8 => ((decimal)sbyte.MinValue, (decimal)sbyte.MaxValue),
            DataType.UInt8 => (byte.MinValue, (decimal)byte.MaxValue),
            DataType.Int16 => (short.MinValue, (decimal)short.MaxValue),
            DataType.UInt16 => (ushort.MinValue, (decimal)ushort.MaxValue),
            DataType.Int32 => (int.MinValue, (decimal)int.MaxValue),
            DataType.UInt32 => (uint.MinValue, (decimal)uint.MaxValue),
            DataType.Int64 => (long.MinValue, (decimal)long.MaxValue),
            DataType.UInt64 => (ulong.MinValue, (decimal)ulong.MaxValue),
            _ => throw new ArgumentException("Macro writes support Boolean, integer and floating-point types only.")
        };
        var integer = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        if (decimal.Truncate(integer) != integer || integer < bounds.Item1 || integer > bounds.Item2)
            throw new ArgumentException("Macro integer is fractional or outside the selected data type.");
        if (Math.Abs(integer) > 9007199254740991m)
            throw new ArgumentException("Macro integer exceeds the exact double-precision integer range.");
        return (double)integer;
    }
}
