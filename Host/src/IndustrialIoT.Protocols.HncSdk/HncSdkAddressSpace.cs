namespace IndustrialIoT.Protocols.HncSdk;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

internal sealed record HncStaticMetadata(
    int PathCount,
    IReadOnlyList<int> AxisCountPerChannel)
{
    public int MaxAxisCount => AxisCountPerChannel.Count > 0 ? AxisCountPerChannel.Max() : 1;
}

internal static class HncSdkAddressSpace
{
    public static IReadOnlyList<AddressNode> Browse(string? parentPath, HncStaticMetadata? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(parentPath) || parentPath == "/")
            return Root;
        return parentPath.Trim('/').ToLowerInvariant() switch
        {
            "system" => SystemNodes,
            "channel" => BuildChannelNodes(metadata),
            "axis" => BuildAxisNodes(metadata),
            "var" => VarNodes,
            "param" => ParamNodes,
            "crds" => BuildCrdsNodes(metadata),
            "tool" => ToolNodes,
            "alarm" => AlarmNodes,
            "program" => BuildProgramNodes(metadata),
            _ => [],
        };
    }

    private static readonly IReadOnlyList<AddressNode> Root =
    [
        Folder("/System", "System"),
        Folder("/Channel", "Channel"),
        Folder("/Axis", "Axis"),
        Folder("/Var", "Var"),
        Folder("/Param", "Param"),
        Folder("/Crds", "Crds"),
        Folder("/Tool", "Tool"),
        Folder("/Alarm", "Alarm"),
        Folder("/Program", "Program"),
    ];

    private static readonly IReadOnlyList<AddressNode> SystemNodes =
    [
        Variable("sys:NC_VER", "NC version (HNC_SYS_NC_VER)", DataType.String, false),
        Variable("sys:CHAN_NUM", "Channel count (HNC_SYS_CHAN_NUM)", DataType.Int32, false),
        Variable("sys:CNC_VER", "CNC version (HNC_SYS_CNC_VER)", DataType.Int32, false),
        Variable("sys:MACHINE_TYPE", "Machine type (HNC_SYS_MACHINE_TYPE)", DataType.String, false),
        Variable("sys:ACTIVE_CHAN", "Active channel (HNC_SYS_ACTIVE_CHAN)", DataType.Int32, false),
    ];

    private static readonly string[] ChannelIntEnums =
        ["MODE", "FEED_OVERRIDE", "RAPID_OVERRIDE", "SPDL_OVERRIDE", "IS_RUNNING", "IS_HOMING", "IS_MOVING",
         "RUN_PROG", "SEL_PROG", "RUN_ROW", "DCD_ROW", "PART_CNTR", "MCODE", "TCODE", "TOOL_USE"];

    private static readonly string[] ChannelDoubleEnums =
        ["ACT_FEEDRATE", "CMD_FEEDRATE", "PROG_FEEDRATE", "ACT_SPDL_SPEED", "CMD_SPDL_SPEED"];

    private static readonly string[] ChannelStringEnums =
        ["NAME", "AXIS_NAME", "SPDL_NAME"];

    private static IReadOnlyList<AddressNode> BuildChannelNodes(HncStaticMetadata? metadata)
    {
        var pathCount = Math.Max(1, metadata?.PathCount ?? 1);
        var nodes = new List<AddressNode>(pathCount * (ChannelIntEnums.Length + ChannelDoubleEnums.Length + ChannelStringEnums.Length));
        for (var ch = 0; ch < pathCount; ch++)
        {
            foreach (var t in ChannelIntEnums)
                nodes.Add(Variable($"chan:{t}:{ch}:0", $"Ch{ch} {t}", DataType.Int32, false));
            foreach (var t in ChannelDoubleEnums)
                nodes.Add(Variable($"chan:{t}:{ch}:0", $"Ch{ch} {t}", DataType.Double, false));
            foreach (var t in ChannelStringEnums)
                nodes.Add(Variable($"chan:{t}:{ch}:0", $"Ch{ch} {t}", DataType.String, false));
        }
        return nodes;
    }

    private static IReadOnlyList<AddressNode> BuildAxisNodes(HncStaticMetadata? metadata)
    {
        var axisCount = Math.Max(1, metadata?.MaxAxisCount ?? 1);
        var nodes = new List<AddressNode>(axisCount * 4);
        for (var ax = 0; ax < axisCount; ax++)
        {
            nodes.Add(Variable($"axis:ACT_POS:{ax}", $"Axis {ax} actual position", DataType.Double, false));
            nodes.Add(Variable($"axis:CMD_POS:{ax}", $"Axis {ax} command position", DataType.Double, false));
            nodes.Add(Variable($"axis:ACT_VEL:{ax}", $"Axis {ax} actual velocity", DataType.Double, false));
            nodes.Add(Variable($"axis:NAME:{ax}", $"Axis {ax} name", DataType.String, false));
        }
        return nodes;
    }

    private static IReadOnlyList<AddressNode> BuildCrdsNodes(HncStaticMetadata? metadata)
    {
        var pathCount = Math.Max(1, metadata?.PathCount ?? 1);
        var axisCount = Math.Max(1, metadata?.MaxAxisCount ?? 1);
        var nodes = new List<AddressNode>(pathCount * axisCount * 2);
        for (var ch = 0; ch < pathCount; ch++)
            for (var ax = 0; ax < axisCount; ax++)
            {
                nodes.Add(Variable($"crds:OFFSET:{ax}:{ch}:54", $"G54 offset ch{ch} ax{ax}", DataType.Double));
                nodes.Add(Variable($"crds:MACHINE:{ax}:{ch}:0", $"Machine pos ch{ch} ax{ax}", DataType.Double, false));
            }
        return nodes;
    }

    private static IReadOnlyList<AddressNode> BuildProgramNodes(HncStaticMetadata? metadata)
    {
        var pathCount = Math.Max(1, metadata?.PathCount ?? 1);
        var nodes = new List<AddressNode>(pathCount);
        for (var ch = 0; ch < pathCount; ch++)
            nodes.Add(Variable($"program:current:{ch}", $"Channel {ch} running program name", DataType.String, false));
        return nodes;
    }

    private static readonly IReadOnlyList<AddressNode> VarNodes =
    [
        Variable("var:G:54:0", "G variable G54 axis 0 (var:type:no:index)", DataType.Double),
        Variable("var:R:100:0", "R register 100 (var:type:no:index)", DataType.Int32),
    ];

    private static readonly IReadOnlyList<AddressNode> ParamNodes =
    [
        Variable("param:1000", "Parameter id 1000 (param:id[:propType])", DataType.Int32),
        Variable("param:1000:0", "Parameter 1000 prop 0 (PARA_PROP_VALUE)", DataType.Int32),
    ];

    private static readonly IReadOnlyList<AddressNode> ToolNodes =
    [
        Variable("tool:1:0", "Tool 1 parameter index 0 (tool:toolNo:index)", DataType.Double),
        Variable("tool:max", "Max tool number (read only)", DataType.Int32, false),
    ];

    private static readonly IReadOnlyList<AddressNode> AlarmNodes =
    [
        Variable("alarm:count", "Alarm count", DataType.Int32, false),
        Variable("alarm:0", "Alarm 0 text (alarm:index)", DataType.String, false),
    ];

    private static AddressNode Folder(string path, string name) => new()
    {
        Path = path,
        DisplayName = name,
        NodeType = AddressNodeType.Folder,
        IsReadable = false,
        IsWritable = false,
    };

    private static AddressNode Variable(string path, string name, DataType dataType, bool writable = true) => new()
    {
        Path = path,
        DisplayName = name,
        NodeType = AddressNodeType.Variable,
        DataType = dataType,
        IsReadable = true,
        IsWritable = writable,
    };
}
