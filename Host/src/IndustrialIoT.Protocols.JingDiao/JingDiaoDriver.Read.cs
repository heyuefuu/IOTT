namespace IndustrialIoT.Protocols.JingDiao;

using System.Globalization;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

public sealed partial class JingDiaoDriver
{
    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        EnsureConnected();
        var normalized = Normalize(address);
        var parts = normalized.Split([':', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Bad(normalized, dataType, "JingDiao address is empty.");

        return parts[0].ToLowerInvariant() switch
        {
            "pos" => await ReadPositionAsync(normalized, parts, dataType, ct),
            "modal" => await ReadModalAsync(normalized, parts, dataType, ct),
            "state" => await ReadStateAsync(normalized, parts, dataType, ct),
            "spindle" => await ReadSpindleAsync(normalized, parts, dataType, ct),
            "rate" => await ReadRateAsync(normalized, parts, dataType, ct),
            "macro" => await ReadMacroAsync(normalized, parts, dataType, ct),
            _ => Bad(normalized, dataType, $"Unknown JingDiao address '{normalized}'.")
        };
    }

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        var values = new List<TagValue>(requests.Count);
        foreach (var request in requests)
            values.Add(await ReadTagAsync(request.Address, request.DataType, ct));
        return values;
    }

    private async Task<TagValue> ReadPositionAsync(string address, string[] parts, DataType dataType, CancellationToken ct)
    {
        if (parts.Length < 3) return Bad(address, dataType, "Position address requires Pos:{Mach|Abs|Rel}:{X|Y|Z}.");
        var result = await client!.GetMachPosAsync(sessionId, ct);
        if (result.ReturnCode != 0 || result.Value is null) return Bad(address, dataType, Error(result));
        var coords = parts[1].ToLowerInvariant() switch
        {
            "mach" or "machine" => result.Value.Machine,
            "abs" or "absolute" => result.Value.Absolute,
            "rel" or "relative" => result.Value.Relative,
            _ => []
        };
        var index = AxisIndex(parts[2]);
        if (coords.Length == 0 || index < 0 || index >= coords.Length)
            return Bad(address, dataType, $"Unknown JingDiao position address '{address}'.");
        return Good(address, dataType, coords[index]);
    }

    private async Task<TagValue> ReadModalAsync(string address, string[] parts, DataType dataType, CancellationToken ct)
    {
        if (parts.Length < 2) return Bad(address, dataType, "Modal address requires a field name.");
        var result = await client!.GetBasicModalAsync(sessionId, ct);
        if (result.ReturnCode != 0 || result.Value is null) return Bad(address, dataType, Error(result));
        object? value = parts[1].ToLowerInvariant() switch
        {
            "wcoord" or "workcoordinate" => result.Value.WorkCoordinate,
            "feedrate" => result.Value.Feedrate,
            "spindlespeed" => result.Value.SpindleSpeed,
            "toolno" => result.Value.ToolNo,
            "machtime" or "machiningtime" => result.Value.MachiningTimeMinutes,
            "progno" or "programno" => result.Value.ProgramNo,
            "mainprogno" or "mainprogramno" => result.Value.MainProgramNo,
            _ => null
        };
        return value is null ? Bad(address, dataType, $"Unknown JingDiao modal field '{parts[1]}'.") : Good(address, dataType, value);
    }

    private async Task<TagValue> ReadStateAsync(string address, string[] parts, DataType dataType, CancellationToken ct)
    {
        if (parts.Length < 2) return Bad(address, dataType, "State address requires a field name.");
        JingDiaoValueResult<int> result = parts[1].ToLowerInvariant() switch
        {
            "prog" or "program" => await client!.GetProgStateAsync(sessionId, ct),
            "alarm" => await client!.GetAlarmAsync(sessionId, ct),
            "lineno" or "line" => await client!.GetLineNoAsync(sessionId, ct),
            "partcount" or "parts" => await client!.GetPartCountAsync(sessionId, ct),
            _ => new() { ReturnCode = -1, ErrorMessage = $"Unknown JingDiao state field '{parts[1]}'." }
        };
        return result.ReturnCode == 0 ? Good(address, dataType, result.Value) : Bad(address, dataType, Error(result));
    }

    private async Task<TagValue> ReadSpindleAsync(string address, string[] parts, DataType dataType, CancellationToken ct)
    {
        if (parts.Length < 2) return Bad(address, dataType, "Spindle address requires Current, Torque, Speed, or Power.");
        if (parts[1].Equals("speed", StringComparison.OrdinalIgnoreCase))
        {
            var modal = await client!.GetBasicModalAsync(sessionId, ct);
            return modal.ReturnCode == 0 && modal.Value is not null
                ? Good(address, dataType, modal.Value.SpindleSpeed)
                : Bad(address, dataType, Error(modal));
        }
        var result = await client!.GetSpindleAsync(sessionId, ct);
        if (result.ReturnCode != 0 || result.Value is null) return Bad(address, dataType, Error(result));
        var value = parts[1].ToLowerInvariant() switch
        {
            "current" => result.Value.Current,
            "torque" => result.Value.Torque,
            "power" => result.Value.Power,
            _ => double.NaN
        };
        return double.IsNaN(value) ? Bad(address, dataType, $"Unknown JingDiao spindle field '{parts[1]}'.") : Good(address, dataType, value);
    }

    private async Task<TagValue> ReadRateAsync(string address, string[] parts, DataType dataType, CancellationToken ct)
    {
        if (parts.Length < 2) return Bad(address, dataType, "Rate address requires Feed or Spindle.");
        var result = await client!.GetRateAsync(sessionId, ct);
        if (result.ReturnCode != 0 || result.Value is null) return Bad(address, dataType, Error(result));
        var value = parts[1].ToLowerInvariant() switch
        {
            "spindle" => result.Value.Spindle,
            "feed" => result.Value.Feed,
            _ => -1
        };
        return value < 0 ? Bad(address, dataType, $"Unknown JingDiao rate field '{parts[1]}'.") : Good(address, dataType, value);
    }

    private async Task<TagValue> ReadMacroAsync(string address, string[] parts, DataType dataType, CancellationToken ct)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out var number))
            return Bad(address, dataType, "Macro address requires Macro:{number}.");
        var result = await client!.GetMacroAsync(sessionId, number, ct);
        return result.ReturnCode == 0 ? Good(address, dataType, result.Value) : Bad(address, dataType, Error(result));
    }

    private static TagValue Good(string address, DataType dataType, object? value) => new()
    {
        Address = address,
        DataType = dataType,
        Value = ConvertValue(value, dataType),
        Quality = TagQuality.Good,
        Timestamp = DateTimeOffset.UtcNow
    };

    private static TagValue Bad(string address, DataType dataType, string? error) => new()
    {
        Address = address,
        DataType = dataType,
        Value = dataType == DataType.String ? "" : 0,
        Quality = TagQuality.Bad,
        Timestamp = DateTimeOffset.UtcNow,
        ErrorMessage = error
    };

    private static object ConvertValue(object? value, DataType dataType)
    {
        if (value is null) return dataType == DataType.String ? "" : 0;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return dataType switch
        {
            DataType.String => text,
            DataType.Bool => bool.TryParse(text, out var b) ? b : text == "1",
            DataType.Int16 => short.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var i16) ? i16 : (short)0,
            DataType.Int32 => int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var i32) ? i32 : 0,
            DataType.Int64 => long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var i64) ? i64 : 0L,
            DataType.UInt16 => ushort.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var u16) ? u16 : (ushort)0,
            DataType.UInt32 => uint.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var u32) ? u32 : 0u,
            DataType.Float => float.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var f) ? f : 0f,
            DataType.Double => double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0d,
            _ => value
        };
    }

    private static int AxisIndex(string axis) => axis.ToUpperInvariant() switch
    {
        "X" => 0,
        "Y" => 1,
        "Z" => 2,
        _ => int.TryParse(axis, out var i) ? i : -1
    };

    private static string Normalize(string address) => address.Trim().TrimStart('/');
    private static string Error(JingDiaoIpcResult result)
        => result.ErrorMessage ?? $"JingDiao IPC call failed: {result.ReturnCode}";
}
