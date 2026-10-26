namespace IndustrialIoT.Protocols.Modbus;

using System.Text;
using System.Text.Json;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

/// <summary>
/// Static address template for generic Modbus/TCP devices.
///
/// Modbus has no browsable namespace, so the device page previously showed an empty tree
/// ("无法选择点位"). Expose the four register areas with 100 points each; every Path is a valid
/// ModbusAddressParser address (HR0 / IR0 / C0 / DI0), so selected points can be read directly.
/// </summary>
internal static class ModbusAddressSpace
{
    public const int PointCount = 100;

    public static IReadOnlyList<AddressNode> Browse(string? parentPath)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
            return
            [
                Folder("HR", "Holding Registers  HR0-HR99 (40001-40100)"),
                Folder("IR", "Input Registers  IR0-IR99 (30001-30100)"),
                Folder("C", "Coils  C0-C99 (00001-00100)"),
                Folder("DI", "Discrete Inputs  DI0-DI99 (10001-10100)"),
                Folder("M", "Internal Relays  M0-M99"),
                Folder("D", "Data Registers  D0-D99"),
                Folder("X", "Input Relays  X0-X99"),
                Folder("Y", "Output Relays  Y0-Y99"),
                Folder("S", "State Relays  S0-S99"),
                Folder("R", "File Registers  R0-R99"),
                Folder("T", "Timers  T0-T99"),
                Folder("CN", "Counters  CN0-CN99"),
                Folder("SM", "Special Relays  SM0-SM99"),
                Folder("SD", "Special Registers  SD0-SD99"),
            ];

        var area = parentPath.Trim().ToUpperInvariant();
        return area switch
        {
            "HR" => Enumerable.Range(0, PointCount)
                .Select(i => Variable($"HR{i}", $"HR{i} (40001+{i})", DataType.UInt16, writable: true)).ToList(),
            "IR" => Enumerable.Range(0, PointCount)
                .Select(i => Variable($"IR{i}", $"IR{i} (30001+{i})", DataType.UInt16, writable: false)).ToList(),
            "C" => Enumerable.Range(0, PointCount)
                .Select(i => Variable($"C{i}", $"C{i} (coil {i})", DataType.Bool, writable: true)).ToList(),
            "DI" => Enumerable.Range(0, PointCount)
                .Select(i => Variable($"DI{i}", $"DI{i} (input {i})", DataType.Bool, writable: false)).ToList(),
            "M" => Enumerable.Range(0, PointCount)
                .Select(i => Variable($"M{i}", $"M{i} (internal relay {i})", DataType.Bool, writable: true)).ToList(),
            "D" => Enumerable.Range(0, PointCount)
                .Select(i => Variable($"D{i}", $"D{i} (data register {i})", DataType.UInt16, writable: true)).ToList(),
            "X" => Enumerable.Range(0, PointCount)
                .Select(i => Variable($"X{i}", $"X{i} (input relay {i})", DataType.Bool, writable: false)).ToList(),
            "Y" => Enumerable.Range(0, PointCount)
                .Select(i => Variable($"Y{i}", $"Y{i} (output relay {i})", DataType.Bool, writable: true)).ToList(),
            "S" => Enumerable.Range(0, PointCount)
                .Select(i => Variable($"S{i}", $"S{i} (state relay {i})", DataType.Bool, writable: true)).ToList(),
            "R" => Enumerable.Range(0, PointCount)
                .Select(i => Variable($"R{i}", $"R{i} (file register {i})", DataType.UInt16, writable: true)).ToList(),
            "T" => Enumerable.Range(0, PointCount)
                .Select(i => Variable($"T{i}", $"T{i} (timer value {i})", DataType.UInt16, writable: false)).ToList(),
            "CN" => Enumerable.Range(0, PointCount)
                .Select(i => Variable($"CN{i}", $"CN{i} (counter value {i})", DataType.UInt16, writable: false)).ToList(),
            "SM" => Enumerable.Range(0, PointCount)
                .Select(i => Variable($"SM{i}", $"SM{i} (special relay {i})", DataType.Bool, writable: false)).ToList(),
            "SD" => Enumerable.Range(0, PointCount)
                .Select(i => Variable($"SD{i}", $"SD{i} (special register {i})", DataType.UInt16, writable: false)).ToList(),
            _ => [],
        };
    }

    public static IEnumerable<(string Path, string DisplayName, DataType DataType, bool Readable, bool Writable)> Enumerate()
    {
        for (var i = 0; i < PointCount; i++) yield return ($"HR{i}", $"HR{i}", DataType.UInt16, true, true);
        for (var i = 0; i < PointCount; i++) yield return ($"IR{i}", $"IR{i}", DataType.UInt16, true, false);
        for (var i = 0; i < PointCount; i++) yield return ($"C{i}", $"C{i}", DataType.Bool, true, true);
        for (var i = 0; i < PointCount; i++) yield return ($"DI{i}", $"DI{i}", DataType.Bool, true, false);
        for (var i = 0; i < PointCount; i++) yield return ($"M{i}", $"M{i}", DataType.Bool, true, true);
        for (var i = 0; i < PointCount; i++) yield return ($"D{i}", $"D{i}", DataType.UInt16, true, true);
        for (var i = 0; i < PointCount; i++) yield return ($"X{i}", $"X{i}", DataType.Bool, true, false);
        for (var i = 0; i < PointCount; i++) yield return ($"Y{i}", $"Y{i}", DataType.Bool, true, true);
        for (var i = 0; i < PointCount; i++) yield return ($"S{i}", $"S{i}", DataType.Bool, true, true);
        for (var i = 0; i < PointCount; i++) yield return ($"R{i}", $"R{i}", DataType.UInt16, true, true);
        for (var i = 0; i < PointCount; i++) yield return ($"T{i}", $"T{i}", DataType.UInt16, true, false);
        for (var i = 0; i < PointCount; i++) yield return ($"CN{i}", $"CN{i}", DataType.UInt16, true, false);
        for (var i = 0; i < PointCount; i++) yield return ($"SM{i}", $"SM{i}", DataType.Bool, true, false);
        for (var i = 0; i < PointCount; i++) yield return ($"SD{i}", $"SD{i}", DataType.UInt16, true, false);
    }

    public static Stream Export(ExportFormat format)
    {
        var items = Enumerate().ToList();
        var text = format == ExportFormat.JSON
            ? JsonSerializer.Serialize(items.Select(n => new
            {
                n.Path, n.DisplayName, DataType = n.DataType.ToString(), n.Readable, n.Writable,
            }))
            : "Path,DisplayName,DataType,Readable,Writable\n" +
              string.Join("\n", items.Select(n => $"{n.Path},{n.DisplayName},{n.DataType},{n.Readable},{n.Writable}"));
        return new MemoryStream(Encoding.UTF8.GetBytes(text));
    }

    private static AddressNode Folder(string path, string name) => new()
    {
        Path = path, DisplayName = name, NodeType = AddressNodeType.Folder,
    };

    private static AddressNode Variable(string path, string name, DataType dataType, bool writable) => new()
    {
        Path = path, DisplayName = name, NodeType = AddressNodeType.Variable,
        DataType = dataType, IsReadable = true, IsWritable = writable,
    };
}
