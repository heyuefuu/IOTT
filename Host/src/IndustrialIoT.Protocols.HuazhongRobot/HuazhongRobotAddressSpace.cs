namespace IndustrialIoT.Protocols.HuazhongRobot;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

/// <summary>
/// 华中机器人 HSR / HR / HC 系列 Modbus/TCP 地址映射（实例化，按设备/配置注入）。
/// 默认无内置映射 — 真实地址必须通过下列任一方式提供：
///   1) appsettings.json 的 "RobotAddressMaps:Huazhong:Nodes" 节点（站点级共享映射）
///   2) <see cref="BatchImportController"/> CSV/JSON 批量导入（设备级映射，存入 CollectionProfile）
///   3) 调用方直传 Hsl 原始 Modbus 地址，跳过路径解析：
///        线圈/IO        — "0x0000"（DI/DO/SI/SO，Coil 区）
///        保持寄存器     — "100"、"4x100"（状态/坐标整型）
///        Float          — "1000;float"（占 2 个 16-bit 寄存器，HslCommunication 解析）
///
/// 设计原因：华中机器人无统一公开 Modbus 寄存器手册，HSR/HR/HC 各系列、各固件版本、
/// 各集成商现场配置的寄存器映射均不同 — 硬编码"通用映射"会误导生产部署。
/// </summary>
public sealed class HuazhongRobotAddressSpace
{
    public sealed record Node(string Path, string DisplayName, string ModbusAddress, DataType DataType, bool IsWritable);

    private readonly Dictionary<string, Node> _byPath;
    private readonly IReadOnlyList<Node> _all;

    public HuazhongRobotAddressSpace() : this(null) { }

    public HuazhongRobotAddressSpace(IEnumerable<Node>? nodes)
    {
        _all = (nodes ?? Enumerable.Empty<Node>()).ToList();
        _byPath = _all.ToDictionary(n => n.Path, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<Node> All => _all;

    /// <summary>按配置路径查找底层 Modbus 地址；未命中时回退为原字符串（支持直传 "4x100"、"1000;float" 等真机原地址）。</summary>
    public (string ModbusAddress, DataType DataType, bool IsWritable) Resolve(string pathOrAddress, DataType requestedType)
    {
        if (_byPath.TryGetValue(pathOrAddress, out var n))
            return (n.ModbusAddress, n.DataType, n.IsWritable);
        return (pathOrAddress, requestedType, true);
    }

    public IReadOnlyList<AddressNode> BuildTree()
    {
        if (_all.Count == 0) return Array.Empty<AddressNode>();
        var groups = _all.GroupBy(n =>
        {
            var seg = n.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return seg.Length >= 2 ? $"/{seg[0]}/{seg[1]}" : n.Path;
        });
        return groups.Select(g => new AddressNode
        {
            Path = g.Key,
            DisplayName = g.Key.TrimStart('/'),
            NodeType = AddressNodeType.Folder,
            IsReadable = false,
            Children = g.Select(n => new AddressNode
            {
                Path = n.Path,
                DisplayName = n.DisplayName,
                NodeType = AddressNodeType.Variable,
                DataType = n.DataType,
                IsReadable = true,
                IsWritable = n.IsWritable,
            }).ToList(),
        }).ToList();
    }
}
