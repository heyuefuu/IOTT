namespace IndustrialIoT.Protocols.NCLinkApi;

using System.Text;
using System.Text.Json;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;

/// <summary>
/// 静态地址树 — 完全按《NC-Link应用开发指导手册》第 8 章数据字典构造。
/// 上层浏览到的 Path 字段可直接作为 ReadTag/WriteTag 的 address 使用。
/// </summary>
public sealed partial class NCLinkApiDriver
{
    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        var tree = BuildStandardTree();
        if (string.IsNullOrEmpty(parentPath))
            return Task.FromResult<IReadOnlyList<AddressNode>>(tree);

        var normalized = parentPath.TrimEnd('/');
        var hit = FindNodeByPath(tree, normalized);
        if (hit is null) return Task.FromResult<IReadOnlyList<AddressNode>>([]);
        if (hit.NodeType == AddressNodeType.Folder)
            return Task.FromResult<IReadOnlyList<AddressNode>>(hit.Children?.ToList() ?? []);
        return Task.FromResult<IReadOnlyList<AddressNode>>([hit]);
    }

    public async Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var nodes = await BrowseAsync(null, ct).ConfigureAwait(false);
        var flattened = FlattenNodes(nodes).ToList();
        var sb = new StringBuilder();
        if (format == ExportFormat.CSV)
        {
            sb.AppendLine("Path,DisplayName,DataType,Readable,Writable");
            foreach (var n in flattened)
                sb.AppendLine($"{n.Path},{n.DisplayName},{n.DataType},{n.IsReadable},{n.IsWritable}");
        }
        else
        {
            sb.Append(JsonSerializer.Serialize(flattened, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));
        }
        return new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static IReadOnlyList<AddressNode> BuildStandardTree() =>
    [
        new()
        {
            Path = "/MACHINE", DisplayName = "MACHINE 机床根", NodeType = AddressNodeType.Folder,
            IsReadable = false, IsWritable = false,
            Children =
            [
                Var(NCLinkApiPaths.MachineStatus, "机床状态", DataType.String, false),
                Var(NCLinkApiPaths.FeedSpeed, "进给速度", DataType.Double, false),
                Var(NCLinkApiPaths.FeedOverride, "进给倍率", DataType.Int32, false),
                Var(NCLinkApiPaths.SpindleOverride, "主轴倍率 (≤2.40)", DataType.Int32, false),
                Var(NCLinkApiPaths.SpindleOverrideV241, "主轴倍率 (2.41+)", DataType.Int32, false),
                Var(NCLinkApiPaths.PartCount, "加工件数", DataType.Int32, false),
                Var(NCLinkApiPaths.ProgramNumber + "?index=1", "程序号 (index=1)", DataType.Int32, false),
                new()
                {
                    Path = NCLinkApiPaths.Controller, DisplayName = "CONTROLLER 控制器", NodeType = AddressNodeType.Folder,
                    IsReadable = false, IsWritable = false,
                    Children =
                    [
                        Var(NCLinkApiPaths.Program, "当前程序", DataType.String, false),
                        Var(NCLinkApiPaths.Warning, "报警信息", DataType.String, false),
                        Var(NCLinkApiPaths.Parameter + "?index=100", "参数 (index=100)", DataType.String, false),
                        Var(NCLinkApiPaths.ToolParam + "?index=1", "刀具参数 (index=1)", DataType.String, false),
                        Var(NCLinkApiPaths.Coordinate + "?index=1", "坐标系 (index=1)", DataType.String, false),
                        Var(NCLinkApiPaths.Console, "控制台指令 (write-only)", DataType.String, true),
                        Var(NCLinkApiPaths.File, "G 代码目录", DataType.String, false),
                        new()
                        {
                            Path = "/MACHINE/CONTROLLER/VARIABLE", DisplayName = "VARIABLE 变量",
                            NodeType = AddressNodeType.Folder,
                            Children = BuildVariableChildren(),
                        },
                    ],
                },
            ],
        },
    ];

    private static IReadOnlyList<AddressNode> BuildVariableChildren()
    {
        var nodes = new List<AddressNode>
        {
            Var(NCLinkApiPaths.VariableSys + "?index=11", "系统数据-NCK版本 (index=11)", DataType.String, false),
            Var(NCLinkApiPaths.VariableSys + "?index=25", "系统数据-装置SN (index=25)", DataType.String, false),
            Var(NCLinkApiPaths.VariableMacro + "?index=10000", "宏变量 (index=10000)", DataType.Double, false),
            Var(NCLinkApiPaths.VariableSysSmpl, "采样客户端管理", DataType.String, false),
        };

        foreach (var name in new[] { "X", "Y", "R", "F", "G", "B" })
        {
            nodes.Add(Var(
                $"/MACHINE/CONTROLLER/VARIABLE@REG_{name}?index=0",
                $"寄存器 {name} (index=0)", DataType.Int32, true));
        }

        for (var ch = 0; ch < 4; ch++)
        {
            nodes.Add(Var(
                NCLinkApiPaths.VariableChan(ch) + "?index=6",
                $"通道 {ch} 指令进给速度", DataType.Float, false));
        }

        var axisNames = new[] { "X", "Y", "Z", "A", "B", "C" };
        for (var i = 0; i < axisNames.Length; i++)
        {
            nodes.Add(Var(
                NCLinkApiPaths.VariableAxis(i) + "?index=38",
                $"{axisNames[i]} 轴实际位置 (浮点)", DataType.Double, false));
        }
        return nodes;
    }

    private static AddressNode Var(string path, string display, DataType dt, bool writable) => new()
    {
        Path = path,
        DisplayName = display,
        NodeType = AddressNodeType.Variable,
        DataType = dt,
        IsReadable = true,
        IsWritable = writable,
    };

    private static AddressNode? FindNodeByPath(IEnumerable<AddressNode> nodes, string path)
    {
        foreach (var n in nodes)
        {
            if (n.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) return n;
            if (n.Children is { Count: > 0 })
            {
                var sub = FindNodeByPath(n.Children, path);
                if (sub is not null) return sub;
            }
        }
        return null;
    }

    private static IEnumerable<AddressNode> FlattenNodes(IReadOnlyList<AddressNode> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.NodeType == AddressNodeType.Variable) yield return n;
            if (n.Children is not null)
                foreach (var c in FlattenNodes(n.Children)) yield return c;
        }
    }
}
