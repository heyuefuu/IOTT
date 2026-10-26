namespace IottDevMock.Mock;

/// <summary>按协议生成地址空间树（目录层级 + 变量），并提供按 parentPath 的即时浏览。</summary>
public static class AddressSpaceFactory
{
    public static IReadOnlyList<AddressNodeDto> Browse(MockDevice device, string? parentPath)
    {
        var top = BuildTop(device);
        if (string.IsNullOrWhiteSpace(parentPath) || parentPath == "/" || parentPath == "root")
            return top;

        var normalized = Normalize(parentPath);
        var node = Find(top, normalized);
        return node?.Children ?? [];
    }

    private static string Normalize(string path) => "/" + path.Trim().Trim('/');

    private static AddressNodeDto? Find(IEnumerable<AddressNodeDto> nodes, string normalizedPath)
    {
        foreach (var n in nodes)
        {
            if (Normalize(n.Path) == normalizedPath) return n;
            if (n.Children is { Count: > 0 })
            {
                var hit = Find(n.Children, normalizedPath);
                if (hit is not null) return hit;
            }
        }
        return null;
    }

    private static List<AddressNodeDto> BuildTop(MockDevice d) => d.Protocol switch
    {
        "OpcUa" => OpcUa(),
        "FOCAS" => Focas(),
        "NCLink" or "NCLinkApi" => NcLink(),
        "GskWebServer" => Gsk(),
        "JingDiao" => JingDiao(),
        "HncSdk" => HncSdk(),
        "ModbusTCP" or "ModbusRTU" => Modbus(),
        "SiemensS7" => Siemens(),
        "FINS" or "OmronHostLink" => Fins(),
        "Mewtocol" or "MewtocolSerial" => Mewtocol(),
        "Inovance" or "InovanceSerial" or "InovanceSerialOverTcp" => Inovance(),
        _ => Generic(),
    };

    private static AddressNodeDto[] Range(string folder, string prefix, string dtype, int count, int start = 0)
    {
        var nodes = new AddressNodeDto[count];
        for (var i = 0; i < count; i++)
        {
            var addr = $"{prefix}{start + i}";
            nodes[i] = AddressNodeDto.Var($"/{folder}/{addr}", addr, dtype);
        }
        return nodes;
    }

    private static List<AddressNodeDto> OpcUa() =>
    [
        AddressNodeDto.Folder("/Objects", "Objects",
            AddressNodeDto.Folder("/Objects/CNC", "CNC",
                AddressNodeDto.Folder("/Objects/CNC/Static", "Static",
                    AddressNodeDto.Var("/Objects/CNC/Static/AxisCount", "AxisCount", "Int32", false),
                    AddressNodeDto.Var("/Objects/CNC/Static/SpindleCount", "SpindleCount", "Int32", false),
                    AddressNodeDto.Var("/Objects/CNC/Static/PathCount", "PathCount", "Int32", false),
                    AddressNodeDto.Var("/Objects/CNC/Static/SystemType", "SystemType", "String", false)),
                AddressNodeDto.Folder("/Objects/CNC/Axes", "Axes",
                    AxisFolder("/Objects/CNC/Axes/X", "X"),
                    AxisFolder("/Objects/CNC/Axes/Y", "Y"),
                    AxisFolder("/Objects/CNC/Axes/Z", "Z")),
                AddressNodeDto.Folder("/Objects/CNC/Macro", "Macro",
                    AddressNodeDto.Var("/Objects/CNC/Macro/macro:1", "macro:1", "Double"),
                    AddressNodeDto.Var("/Objects/CNC/Macro/macro:2", "macro:2", "Double")))),
    ];

    private static AddressNodeDto AxisFolder(string path, string label) =>
        AddressNodeDto.Folder(path, $"Axis{label}",
            AddressNodeDto.Var($"{path}/Position", "Position", "Double", false),
            AddressNodeDto.Var($"{path}/Load", "Load", "Double", false),
            AddressNodeDto.Var($"{path}/Speed", "Speed", "Double", false));

    private static List<AddressNodeDto> Focas() =>
    [
        AddressNodeDto.Folder("/CNC", "CNC",
            AddressNodeDto.Folder("/CNC/Status", "Status",
                AddressNodeDto.Var("/CNC/Status/RunStatus", "RunStatus", "Int32", false),
                AddressNodeDto.Var("/CNC/Status/ProgramName", "ProgramName", "String", false),
                AddressNodeDto.Var("/CNC/Status/LineNo", "LineNo", "Int32", false)),
            AddressNodeDto.Folder("/CNC/Axis", "Axis",
                AxisFolder("/CNC/Axis/AxisX", "X"),
                AxisFolder("/CNC/Axis/AxisY", "Y"),
                AxisFolder("/CNC/Axis/AxisZ", "Z")),
            AddressNodeDto.Folder("/CNC/PMC", "PMC",
                AddressNodeDto.Var("/CNC/PMC/DI0", "DI0", "Bool", false),
                AddressNodeDto.Var("/CNC/PMC/DO0", "DO0", "Bool"),
                AddressNodeDto.Var("/CNC/PMC/RI0", "RI0", "Bool", false),
                AddressNodeDto.Var("/CNC/PMC/RO0", "RO0", "Bool"))),
    ];

    private static List<AddressNodeDto> NcLink() =>
    [
        AddressNodeDto.Folder("/Components", "Components",
            AddressNodeDto.Folder("/Components/Spindle", "Spindle",
                AddressNodeDto.Var("/Components/Spindle/Speed", "Speed", "Double", false),
                AddressNodeDto.Var("/Components/Spindle/Load", "Load", "Double", false)),
            AddressNodeDto.Folder("/Components/Feed", "Feed",
                AddressNodeDto.Var("/Components/Feed/Rate", "Rate", "Double", false)),
            AddressNodeDto.Folder("/Components/Program", "Program",
                AddressNodeDto.Var("/Components/Program/Name", "Name", "String", false),
                AddressNodeDto.Var("/Components/Program/Number", "Number", "Int32", false))),
    ];

    private static List<AddressNodeDto> Gsk() =>
    [
        AddressNodeDto.Folder("/Realtime", "Realtime",
            AddressNodeDto.Var("/Realtime/Running", "Running", "Bool", false),
            AddressNodeDto.Var("/Realtime/Mode", "Mode", "Int32", false),
            AddressNodeDto.Var("/Realtime/ProgramName", "ProgramName", "String", false)),
        AddressNodeDto.Folder("/Macro", "Macro",
            AddressNodeDto.Var("/Macro/1", "Macro:1", "Double"),
            AddressNodeDto.Var("/Macro/2", "Macro:2", "Double")),
    ];

    private static List<AddressNodeDto> JingDiao() =>
    [
        AddressNodeDto.Folder("/Pos", "Pos",
            AddressNodeDto.Var("/Pos/X", "X", "Double", false),
            AddressNodeDto.Var("/Pos/Y", "Y", "Double", false),
            AddressNodeDto.Var("/Pos/Z", "Z", "Double", false)),
        AddressNodeDto.Folder("/State", "State",
            AddressNodeDto.Var("/State/Prog", "Prog", "String", false),
            AddressNodeDto.Var("/State/LineNo", "LineNo", "Int32", false),
            AddressNodeDto.Var("/State/PartCount", "PartCount", "Int32", false)),
    ];

    private static List<AddressNodeDto> HncSdk() =>
    [
        AddressNodeDto.Folder("/sys", "sys",
            AddressNodeDto.Var("/sys/NC_VER", "NC_VER", "String", false),
            AddressNodeDto.Var("/sys/CHAN_NUM", "CHAN_NUM", "Int32", false)),
        AddressNodeDto.Folder("/var", "var",
            AddressNodeDto.Var("/var/1", "var:1", "Double"),
            AddressNodeDto.Var("/var/2", "var:2", "Double")),
    ];

    private static List<AddressNodeDto> Modbus() =>
    [
        AddressNodeDto.Folder("/HoldingRegisters", "HoldingRegisters", Range("HoldingRegisters", "HR", "UInt16", 10)),
        AddressNodeDto.Folder("/Coils", "Coils", Range("Coils", "C", "Bool", 10)),
        AddressNodeDto.Folder("/InputRegisters", "InputRegisters", Range("InputRegisters", "IR", "UInt16", 5)),
        AddressNodeDto.Folder("/DiscreteInputs", "DiscreteInputs", Range("DiscreteInputs", "DI", "Bool", 5)),
    ];

    private static List<AddressNodeDto> Siemens() =>
    [
        AddressNodeDto.Folder("/Markers", "Markers",
            AddressNodeDto.Var("/Markers/MW0", "MW0", "Int16"),
            AddressNodeDto.Var("/Markers/MW2", "MW2", "Int16"),
            AddressNodeDto.Var("/Markers/MD4", "MD4", "Int32"),
            AddressNodeDto.Var("/Markers/M100.0", "M100.0", "Bool")),
        AddressNodeDto.Folder("/Inputs", "Inputs",
            AddressNodeDto.Var("/Inputs/IW0", "IW0", "Int16", false),
            AddressNodeDto.Var("/Inputs/I0.0", "I0.0", "Bool", false)),
        AddressNodeDto.Folder("/Outputs", "Outputs",
            AddressNodeDto.Var("/Outputs/QW0", "QW0", "Int16"),
            AddressNodeDto.Var("/Outputs/Q0.0", "Q0.0", "Bool")),
        AddressNodeDto.Folder("/DataBlocks", "DataBlocks",
            AddressNodeDto.Folder("/DataBlocks/DB1", "DB1",
                AddressNodeDto.Var("/DataBlocks/DB1/DB1.DBW0", "DB1.DBW0", "Int16"),
                AddressNodeDto.Var("/DataBlocks/DB1/DB1.DBD2", "DB1.DBD2", "Int32"),
                AddressNodeDto.Var("/DataBlocks/DB1/DB1.DBX4.0", "DB1.DBX4.0", "Bool"))),
    ];

    private static List<AddressNodeDto> Fins() =>
    [
        AddressNodeDto.Folder("/DM", "DM", Range("DM", "DM", "Int16", 10)),
        AddressNodeDto.Folder("/CIO", "CIO", Range("CIO", "CIO", "Int16", 5)),
        AddressNodeDto.Folder("/WR", "WR", Range("WR", "WR", "Int16", 5)),
        AddressNodeDto.Folder("/AR", "AR", Range("AR", "AR", "Int16", 5)),
    ];

    private static List<AddressNodeDto> Mewtocol() =>
    [
        AddressNodeDto.Folder("/DT", "DT", Range("DT", "DT", "Int16", 10)),
        AddressNodeDto.Folder("/X", "X", Range("X", "X", "Bool", 8)),
        AddressNodeDto.Folder("/Y", "Y", Range("Y", "Y", "Bool", 8)),
        AddressNodeDto.Folder("/R", "R", Range("R", "R", "Bool", 8)),
    ];

    private static List<AddressNodeDto> Inovance() =>
    [
        AddressNodeDto.Folder("/D", "D", Range("D", "D", "Int16", 10)),
        AddressNodeDto.Folder("/M", "M", Range("M", "M", "Bool", 10)),
        AddressNodeDto.Folder("/X", "X", Range("X", "X", "Bool", 8)),
        AddressNodeDto.Folder("/Y", "Y", Range("Y", "Y", "Bool", 8)),
    ];

    private static List<AddressNodeDto> Generic() =>
    [
        AddressNodeDto.Folder("/Data", "Data", Range("Data", "V", "Int16", 10)),
    ];
}
