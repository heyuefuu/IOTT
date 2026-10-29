namespace IndustrialIoT.Protocols.Addressing;

using System.Text;
using System.Text.Json;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;

/// <summary>Pages a declared macro address range while retaining existing controller status folders.</summary>
public sealed class CncMacroRangeBrowser : IAddressSpaceBrowser
{
    private readonly string _root;
    private readonly string _folderRoot;
    private readonly Lazy<PlcAddressRangeBrowser> _ranges;
    private readonly Func<string?, CancellationToken, Task<IReadOnlyList<AddressNode>>> _browse;
    private readonly Func<string, bool> _writable;

    public CncMacroRangeBrowser(DeviceConnectionConfig config, string root, int maximum,
        Func<int, string> address, Func<string, bool> writable,
        Func<string?, CancellationToken, Task<IReadOnlyList<AddressNode>>> browse,
        int minimum = 1, bool requireExplicitRanges = false)
    {
        _root = root.Trim('/');
        _folderRoot = root.TrimEnd('/');
        _browse = browse;
        _writable = writable;
        var definitions = new[] { new PlcAddressArea("MACRO", "Macro variables", minimum, maximum,
            DataType.Double, address) };
        var configured = config.ExtendedProperties.Keys.Any(key => key.Equals("AddressSpaceRanges", StringComparison.OrdinalIgnoreCase));
        _ranges = new(() => new(PlcAddressRangeBrowser.Configure(definitions, config, requireExplicitRanges),
            configured ? "configured-controller-range" : "declared-protocol-range",
            "Declared address range; actual availability and access permissions depend on the controller options and point table."));
    }

    public async Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        var path = (parentPath ?? "").Trim('/');
        if (path.Equals(_root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(_root + "/", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = path.Length == _root.Length ? "" : path[(_root.Length + 1)..];
            var nodes = await _ranges.Value.BrowseAsync("MACRO" + (suffix.Length == 0 ? "" : "/" + suffix), ct);
            return nodes.Select(node => node.NodeType == AddressNodeType.Folder
                ? node with { Path = _folderRoot + node.Path["MACRO".Length..] }
                : node with { IsWritable = _writable(node.Path) }).ToArray();
        }
        return await _browse(parentPath, ct);
    }

    public async Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        if (format is not (ExportFormat.CSV or ExportFormat.JSON))
            throw new NotSupportedException("CNC address catalogs support CSV and JSON exports.");
        var stream = new FileStream(Path.Combine(Path.GetTempPath(), $"cnc-address-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            if (format == ExportFormat.JSON)
                await JsonSerializer.SerializeAsync(stream, EnumerateAsync(ct), cancellationToken: ct);
            else
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, leaveOpen: true);
                await writer.WriteLineAsync("Path,DisplayName,DataType,Readable,Writable,Source,Description".AsMemory(), ct);
                await foreach (var node in EnumerateAsync(ct))
                    await writer.WriteLineAsync(string.Join(",", new[] { node.Path, node.DisplayName, node.DataType.ToString(),
                        node.IsReadable.ToString(), node.IsWritable.ToString(), node.Source, node.Description }.Select(Csv)).AsMemory(), ct);
                await writer.FlushAsync(ct);
            }
            stream.Position = 0;
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    private async IAsyncEnumerable<AddressNode> EnumerateAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var pending = new Queue<string?>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var points = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(null);
        while (pending.TryDequeue(out var path))
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(path ?? "")) continue;
            foreach (var node in await BrowseAsync(path, ct))
            {
                if (node.NodeType == AddressNodeType.Folder) pending.Enqueue(node.Path);
                else if (points.Add(node.Path)) yield return node;
            }
        }
    }

    private static string Csv(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
}
