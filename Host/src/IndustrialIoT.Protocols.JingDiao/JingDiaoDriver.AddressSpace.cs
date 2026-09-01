namespace IndustrialIoT.Protocols.JingDiao;

using System.Text;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

public sealed partial class JingDiaoDriver
{
    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        IReadOnlyList<AddressNode> nodes = string.IsNullOrWhiteSpace(parentPath) || parentPath == "/"
            ? RootNodes
            : ChildNodes(parentPath.Trim('/'));
        return Task.FromResult(nodes);
    }

    public Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var sb = new StringBuilder("Path,DisplayName,DataType,Readable,Writable\n");
        foreach (var root in RootNodes)
        foreach (var node in ChildNodes(root.Path.Trim('/')))
            sb.AppendLine($"{node.Path},{node.DisplayName},{node.DataType},{node.IsReadable},{node.IsWritable}");
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static readonly IReadOnlyList<AddressNode> RootNodes =
    [
        Folder("/Pos", "Position"),
        Folder("/Modal", "Modal"),
        Folder("/State", "State"),
        Folder("/Spindle", "Spindle"),
        Folder("/Rate", "Override rate"),
        Folder("/Macro", "Macro variables"),
        Folder("/Program", "NC program files"),
    ];

    private static IReadOnlyList<AddressNode> ChildNodes(string group) => group.ToLowerInvariant() switch
    {
        "pos" =>
        [
            Var("Pos:Mach:X", "Machine X", DataType.Double),
            Var("Pos:Mach:Y", "Machine Y", DataType.Double),
            Var("Pos:Mach:Z", "Machine Z", DataType.Double),
            Var("Pos:Abs:X", "Absolute X", DataType.Double),
            Var("Pos:Abs:Y", "Absolute Y", DataType.Double),
            Var("Pos:Abs:Z", "Absolute Z", DataType.Double),
            Var("Pos:Rel:X", "Relative X", DataType.Double),
            Var("Pos:Rel:Y", "Relative Y", DataType.Double),
            Var("Pos:Rel:Z", "Relative Z", DataType.Double),
        ],
        "modal" =>
        [
            Var("Modal:Feedrate", "Feedrate", DataType.Float),
            Var("Modal:SpindleSpeed", "Spindle speed", DataType.Int32),
            Var("Modal:ToolNo", "Tool number", DataType.Int32),
            Var("Modal:ProgNo", "Current program number", DataType.Int32),
            Var("Modal:MainProgNo", "Main program number", DataType.Int32),
            Var("Modal:WCoord", "Work coordinate", DataType.Int32),
            Var("Modal:MachTime", "Machining time minutes", DataType.Float),
        ],
        "state" =>
        [
            Var("State:Prog", "Program state", DataType.Int32),
            Var("State:Alarm", "Alarm state", DataType.Int32),
            Var("State:LineNo", "Current line number", DataType.Int32),
            Var("State:PartCount", "Machined part count", DataType.Int32),
        ],
        "spindle" =>
        [
            Var("Spindle:Current", "Spindle current", DataType.Double),
            Var("Spindle:Speed", "Spindle speed", DataType.Int32),
            Var("Spindle:Torque", "Spindle torque", DataType.Double),
            Var("Spindle:Power", "Spindle power", DataType.Double),
        ],
        "rate" =>
        [
            Var("Rate:Feed", "Feed override", DataType.Int32),
            Var("Rate:Spindle", "Spindle override", DataType.Int32),
        ],
        "macro" => [Var("Macro:100", "Macro variable sample", DataType.Double)],
        "program" => [Var("Program.Files", "Program file listing", DataType.String)],
        _ => []
    };

    private static AddressNode Folder(string path, string name) => new()
    {
        Path = path,
        DisplayName = name,
        NodeType = AddressNodeType.Folder,
        IsReadable = false,
        IsWritable = false
    };

    private static AddressNode Var(string path, string name, DataType dataType) => new()
    {
        Path = path,
        DisplayName = name,
        NodeType = AddressNodeType.Variable,
        DataType = dataType,
        IsReadable = true,
        IsWritable = false
    };
}
