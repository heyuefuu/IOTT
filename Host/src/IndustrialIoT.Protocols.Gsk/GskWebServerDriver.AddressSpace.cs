namespace IndustrialIoT.Protocols.Gsk;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

internal sealed record GskStaticMetadata(
    int PathCount,
    int AxisCount,
    int SpindleCount,
    IReadOnlyList<string> AxisNamesAbsolute,
    IReadOnlyList<string> AxisNamesRelative,
    IReadOnlyList<string> AxisNamesMachine,
    IReadOnlyList<string> AxisNamesRemain);

public sealed partial class GskWebServerDriver
{
    private static IReadOnlyList<AddressNode> RootNodes() =>
    [
        Folder("/Realtime", "Realtime"),
        Folder("/Static", "Static"),
        Folder("/Macro", "Macro"),
        Folder("/Param", "Param"),
        Folder("/Diagnose", "Diagnose"),
        Folder("/SoftPlc", "SoftPlc"),
        Folder("/Program", "Program"),
        Folder("/Tool", "Tool"),
        Folder("/ToolLife", "ToolLife"),
        Folder("/Alarm", "Alarm"),
        Folder("/History", "History"),
        Folder("/Workshop", "Workshop")
    ];

    private IReadOnlyList<AddressNode> ChildNodes(string group) => group.ToLowerInvariant() switch
    {
        "realtime" => BuildRealtimeNodes(),
        "static" => BuildStaticNodes(),
        "macro" => [Variable("/Macro:100", "Macro 100 sample (id[:path])", DataType.Double)],
        "param" => [Variable("/Param:1023:0", "Parameter 1023 axis 0 sample (id[:axis])", DataType.Int32)],
        "diagnose" => [Variable("/Diagnose:10:0", "Diagnose 10 axis 0 sample (id[:axis])", DataType.Int32, false)],
        "softplc" => [Variable("/SoftPlc:R:100", "Soft PLC R[100] sample ({type}:{id}, types: x/y/k/a/r)", DataType.Int32)],
        "program" =>
        [
            Variable("/Program.Files", "Program file listing (browse)", DataType.String, false),
            Variable("/Program.Load", "Load NC program by name (write program name)", DataType.String)
        ],
        "tool" =>
        [
            Variable("/Tool.TotalCount", "Total tool-offset count", DataType.Int32, false),
            Variable("/Tool:10", "Tool offset 10 sample ({id}; write: {id}:{path}:{type}:{axis})", DataType.Double)
        ],
        "toollife" =>
        [
            Variable("/ToolLife.TotalCount", "Tool-life group count", DataType.String, false),
            Variable("/ToolLife.Current:1", "Currently running tool of group 1", DataType.String, false),
            Variable("/ToolLife.Attr:1", "Group 1 attribute", DataType.String, false),
            Variable("/ToolLife.Prop:1:0", "Group 1 tool 0 property", DataType.String, false),
            Variable("/ToolLife.Attr:1:0:1:80", "Write group 1 attr ({group}:{path}:{type}:{preset})", DataType.String),
            Variable("/ToolLife.Prop:1:0:0:true:5:6", "Write group 1 tool jump state", DataType.String),
            Variable("/ToolLife.Repo:1:0:0:false:5:6", "Write group 1 tool number and offset", DataType.String),
            Variable("/ToolLife.Delete:1:0", "Delete group 1 tool-life data", DataType.String)
        ],
        "alarm" =>
        [
            Variable("/Alarm.TotalCount", "Current alarm count", DataType.Int32, false),
            Variable("/Alarm:0", "Alarm by index sample", DataType.String, false)
        ],
        "history" =>
        [
            Variable("/History.TotalCount", "Alarm history count", DataType.Int32, false),
            Variable("/History:0", "History entry by index sample", DataType.String, false)
        ],
        "workshop" => [Variable("/Workshop", "Workshop device list/config (port 3000)", DataType.String)],
        _ => []
    };

    private IReadOnlyList<AddressNode> BuildRealtimeNodes()
    {
        var nodes = new List<AddressNode>
        {
            Variable("/Realtime.Mode", "Mode (0=Edit/1=Auto/2=MDI/3=DNC/4=Jog/5=Mpg/6=Ref)", DataType.Int32, false),
            Variable("/Realtime.State", "State (0=Reset/1=Stop/2=Run/3=Pause)", DataType.Int32, false),
            Variable("/Realtime.Running", "Running (state==2)", DataType.Bool, false),
            Variable("/Realtime.ProgramName", "Current program name", DataType.String, false),
            Variable("/Realtime.LineNo", "Current line", DataType.Int32, false),
            Variable("/Realtime.FeedRate", "Actual feed rate", DataType.Double, false),
            Variable("/Realtime.ProgramFeedRate", "Programmed feed rate", DataType.Double, false)
        };

        var spindleCount = Math.Max(1, _staticMetadata?.SpindleCount ?? 0);
        for (var i = 0; i < spindleCount; i++)
        {
            nodes.Add(Variable($"/Realtime.SpindleSpeed:{i}", $"Spindle {i} actual speed", DataType.Double, false));
            nodes.Add(Variable($"/Realtime.SpindleCmdSpeed:{i}", $"Spindle {i} commanded speed", DataType.Double, false));
        }

        var axisCount = Math.Max(1, _staticMetadata?.AxisCount ?? 1);
        for (var axis = 0; axis < axisCount; axis++)
        {
            nodes.Add(Variable($"/Realtime.Cord.Absolute:{axis}", $"Absolute coordinate {AxisLabel(axis, m => m.AxisNamesAbsolute)}", DataType.Double, false));
            nodes.Add(Variable($"/Realtime.Cord.Relative:{axis}", $"Relative coordinate {AxisLabel(axis, m => m.AxisNamesRelative)}", DataType.Double, false));
            nodes.Add(Variable($"/Realtime.Cord.Machine:{axis}", $"Machine coordinate {AxisLabel(axis, m => m.AxisNamesMachine)}", DataType.Double, false));
            nodes.Add(Variable($"/Realtime.Cord.Remain:{axis}", $"Remaining distance {AxisLabel(axis, m => m.AxisNamesRemain)}", DataType.Double, false));
        }

        nodes.AddRange(new[]
        {
            Variable("/Realtime.FeedOverride", "Feed override %", DataType.Int32, false),
            Variable("/Realtime.SpindleOverride", "Spindle override %", DataType.Int32, false),
            Variable("/Realtime.RapidOverride", "Rapid override %", DataType.Int32, false),
            Variable("/Realtime.Esp", "Emergency stop", DataType.Bool, false),
            Variable("/Realtime.Alm", "Alarm flag", DataType.Bool, false),
            Variable("/Realtime.PartsTarget", "Target parts", DataType.Int32, false),
            Variable("/Realtime.PartsCutted", "Parts produced", DataType.Int32, false),
            Variable("/Realtime.RunTime", "Run time (s)", DataType.Int64, false),
            Variable("/Realtime.CutTime", "Cut time (s)", DataType.Int64, false),
            Variable("/Realtime.ToolNo", "Tool number", DataType.Int32, false),
            Variable("/Realtime.OffsetNo", "Tool offset number", DataType.Int32, false)
        });
        return nodes;
    }

    private IReadOnlyList<AddressNode> BuildStaticNodes()
    {
        var nodes = new List<AddressNode>
        {
            Variable("/Static.SystemType", "System type (model)", DataType.String, false),
            Variable("/Static.PathCount", "Channel count", DataType.Int32, false),
            Variable("/Static.AxisCount", "Total axes (channel 0)", DataType.Int32, false)
        };

        var axisCount = Math.Max(1, _staticMetadata?.AxisCount ?? 1);
        foreach (var section in (string[])["Absolute", "Relative", "Machine", "Remain"])
        {
            for (var axis = 0; axis < axisCount; axis++)
            {
                nodes.Add(Variable($"/Static.AxisName.{section}:{axis}", $"{section} axis {axis} name", DataType.String, false));
                nodes.Add(Variable($"/Static.AxisDecimal.{section}:{axis}", $"{section} axis {axis} decimal", DataType.Int32, false));
                nodes.Add(Variable($"/Static.AxisUnit.{section}:{axis}", $"{section} axis {axis} unit (0=mm/1=inch/2=deg)", DataType.Int32, false));
            }
        }

        nodes.Add(Variable("/Static.SpindleCount", "Total spindles (channel 0)", DataType.Int32, false));
        return nodes;
    }

    private string AxisLabel(int axis, Func<GskStaticMetadata, IReadOnlyList<string>> selector)
    {
        var names = _staticMetadata is null ? null : selector(_staticMetadata);
        return names is not null && axis < names.Count && !string.IsNullOrWhiteSpace(names[axis])
            ? $"{names[axis]} (axis {axis})"
            : $"axis {axis}";
    }

    private IEnumerable<AddressNode> KnownLeaves()
    {
        foreach (var root in RootNodes())
        {
            foreach (var child in ChildNodes(root.Path.Trim('/')))
                yield return child;
        }
    }

    private static AddressNode Folder(string path, string name) => new()
    {
        Path = path,
        DisplayName = name,
        NodeType = AddressNodeType.Folder,
        IsReadable = false,
        IsWritable = false
    };

    private static AddressNode Variable(string path, string name, DataType type, bool writable = true) => new()
    {
        Path = path,
        DisplayName = name,
        NodeType = AddressNodeType.Variable,
        DataType = type,
        IsReadable = true,
        IsWritable = writable
    };
}
