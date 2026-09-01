namespace IndustrialIoT.Protocols.Gsk;

using System.Globalization;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

/// <summary>
/// Named-address translator: maps human-readable tag addresses into the
/// corresponding <see cref="IGskrmApi"/> call. Keep this table small and
/// focused — widen as specific tag names become required by integrators.
///
/// Supported address families (case-insensitive):
///   <code>
///   Status.Running          — bool, GetCncState.IsRunning
///   Status.Mode             — string, GetWorkMode
///   Status.ProgramName      — string, GetRunCncProgName
///   Status.LineNo           — int,  GetRunLineNo
///   Status.Estop            — bool, GetEspState
///   Rate.Feed | Spindle | Fast | Jog | HandWheel  — int, GetAllRateInfo
///   Speed.FeedAct | FeedProg | SpindleAct | SpindleProg      — int
///   Position.Abs[:axis] | Machine[:axis] | Relative[:axis]  — double
///   Macro:nnnn             — double, GetMacroValue/SetMacroValue
///   Param:nnnn[:axis]      — int,   GetParamValue/SetParamValue
///   Plc:ADDR[:len]         — bytes, GetPlcData/SetPlcData
///   Tool.OffsetCount       — int,   GetToolOffsetCount
///   Tool.Offset:idx        — double
///   Part.Count             — int
///   Time.Cut | Time.Run    — seconds
///   </code>
/// </summary>
public sealed class GskrmAddressMapper
{
    private readonly IGskrmApi _api;
    private int _handle;

    public GskrmAddressMapper(IGskrmApi api) => _api = api;

    public void SetHandle(int handle) => _handle = handle;

    public TagValue Read(string address, DataType dataType)
    {
        try
        {
            object value = ResolveRead(address, out var quality);
            return new TagValue
            {
                Address = address, DataType = dataType, Value = value,
                Quality = quality, Timestamp = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            return Fail(address, dataType, ex.Message);
        }
    }

    public WriteResult Write(string address, DataType dataType, object value)
    {
        try
        {
            int rc = ResolveWrite(address, value);
            return rc == GskrmErrorCodes.Ok
                ? new WriteResult { Success = true }
                : new WriteResult { Success = false, ErrorMessage = GskrmErrorCodes.Describe(rc) };
        }
        catch (Exception ex)
        {
            return new WriteResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private object ResolveRead(string address, out TagQuality quality)
    {
        quality = TagQuality.Good;
        var (head, tail) = Split(address);

        switch (head.ToLowerInvariant())
        {
            case "status.running":
                Check(_api.GetCncState(_handle, out var st));
                return st.IsRunning;
            case "status.mode":
                Check(_api.GetWorkMode(_handle, out var mode));
                return mode;
            case "status.programname":
                Check(_api.GetRunCncProgName(_handle, out var progName));
                return progName;
            case "status.lineno":
                Check(_api.GetRunLineNo(_handle, out var line));
                return line;
            case "status.estop":
                Check(_api.GetEspState(_handle, out var e));
                return e;
            case "rate.feed": case "rate.fast": case "rate.jog":
            case "rate.spindle": case "rate.handwheel":
                Check(_api.GetAllRateInfo(_handle, out var rates));
                return head.ToLowerInvariant() switch
                {
                    "rate.feed" => rates.FeedRate,
                    "rate.fast" => rates.FastRate,
                    "rate.jog" => rates.JogRate,
                    "rate.spindle" => rates.SpindleRate,
                    _ => rates.HandWheelRate
                };
            case "speed.feedact":    Check(_api.GetFeedSpeedAct(_handle, out var fa)); return fa;
            case "speed.feedprog":   Check(_api.GetFeedSpeedProg(_handle, out var fp)); return fp;
            case "speed.spindleact": Check(_api.GetSpindleSpeedAct(_handle, out var sa)); return sa;
            case "speed.spindleprog":Check(_api.GetSpindleSpeedProg(_handle, out var sp)); return sp;
            case "position.abs": case "position.machine": case "position.relative":
                var axis = tail is null ? 0 : int.Parse(tail, CultureInfo.InvariantCulture);
                Check(_api.GetPosition(_handle, axis, out var pos));
                return head.EndsWith("abs", StringComparison.OrdinalIgnoreCase) ? pos.Absolute
                     : head.EndsWith("machine", StringComparison.OrdinalIgnoreCase) ? pos.Machine
                     : pos.Relative;
            case "macro":
                Check(_api.GetMacroValue(_handle, ParseInt(tail), out var mv));
                return mv;
            case "param":
                var (num, ax) = ParseTwo(tail);
                Check(_api.GetParamValue(_handle, num, ax, out var pv));
                return pv;
            case "plc":
                var (addr, len) = ParsePlc(tail);
                var buf = new byte[len];
                Check(_api.GetPlcData(_handle, addr, len, buf));
                return buf;
            case "tool.offsetcount":
                Check(_api.GetToolOffsetCount(_handle, out var tc));
                return tc;
            case "tool.offset":
                Check(_api.GetToolOffsetValue(_handle, ParseInt(tail), out var tv));
                return tv;
            case "part.count":
                Check(_api.GetPartCount(_handle, out var pc));
                return pc;
            case "time.cut":
                Check(_api.GetCutTime(_handle, out var ct));
                return (int)ct.TotalSeconds;
            case "time.run":
                Check(_api.GetRunTime(_handle, out var rt));
                return (int)rt.TotalSeconds;
            default:
                quality = TagQuality.Bad;
                throw new NotSupportedException($"Unknown GSKRM address '{address}'");
        }
    }

    private int ResolveWrite(string address, object value)
    {
        var (head, tail) = Split(address);
        return head.ToLowerInvariant() switch
        {
            "macro" => _api.SetMacroValue(_handle, ParseInt(tail), Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            "param" => WriteParam(tail, value),
            "plc" => _api.SetPlcData(_handle, ParsePlc(tail).Addr, (byte[])value),
            _ => throw new NotSupportedException($"GSKRM write not supported for '{address}'")
        };
    }

    private int WriteParam(string? tail, object value)
    {
        var (num, axis) = ParseTwo(tail);
        return _api.SetParamValue(_handle, num, axis, Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }

    private static (string Head, string? Tail) Split(string address)
    {
        var colon = address.IndexOf(':');
        return colon < 0 ? (address, null) : (address[..colon], address[(colon + 1)..]);
    }

    private static int ParseInt(string? tail)
    {
        if (string.IsNullOrEmpty(tail))
            throw new FormatException("Numeric suffix required (e.g. 'Macro:10001')");
        return int.Parse(tail, CultureInfo.InvariantCulture);
    }

    private static (int Number, int Axis) ParseTwo(string? tail)
    {
        if (string.IsNullOrEmpty(tail)) throw new FormatException("Format: 'Param:number[:axis]'");
        var parts = tail.Split(':');
        var num = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var axis = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        return (num, axis);
    }

    private static (string Addr, int Length) ParsePlc(string? tail)
    {
        if (string.IsNullOrEmpty(tail)) throw new FormatException("Format: 'Plc:ADDR[:length]'");
        var parts = tail.Split(':');
        var len = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 1;
        return (parts[0], len);
    }

    private static void Check(int rc)
    {
        if (rc != GskrmErrorCodes.Ok)
            throw new InvalidOperationException(GskrmErrorCodes.Describe(rc));
    }

    private static TagValue Fail(string address, DataType dataType, string error) => new()
    {
        Address = address, DataType = dataType, Value = string.Empty,
        Quality = TagQuality.Bad, Timestamp = DateTimeOffset.UtcNow, ErrorMessage = error
    };
}
