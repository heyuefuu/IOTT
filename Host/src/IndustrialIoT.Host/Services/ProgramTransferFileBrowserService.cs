namespace IndustrialIoT.Host.Services;

using System.Text.RegularExpressions;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;

public sealed class ProgramTransferFileBrowserService : IProgramTransferFileBrowserService
{
    private static readonly Regex SizePattern = new(@"\((?<size>\d+(?:\.\d+)?)(?<unit>B|KB|MB)\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<IReadOnlyList<ProgramFileNodeDto>> BrowseAsync(
        IAddressSpaceBrowser browser,
        string? path,
        bool recursive = false,
        CancellationToken ct = default)
    {
        if (!recursive)
            return await BrowseCurrentLevelAsync(browser, path, ct);

        var nodes = new List<ProgramFileNodeDto>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await BrowseRecursiveAsync(browser, path, nodes, visited, ct);
        return nodes;
    }

    private async Task BrowseRecursiveAsync(
        IAddressSpaceBrowser browser,
        string? path,
        List<ProgramFileNodeDto> nodes,
        HashSet<string> visited,
        CancellationToken ct)
    {
        foreach (var node in await BrowseCurrentLevelAsync(browser, path, ct))
        {
            if (!visited.Add(node.Path)) continue;
            nodes.Add(node);
            if (node.NodeType == ProgramFileNodeType.Directory && node.HasChildren)
                await BrowseRecursiveAsync(browser, node.Path, nodes, visited, ct);
        }
    }

    private static ProgramFileNodeDto MapProgramFile(ProgramFileEntry node) => new()
    {
        Path = node.Path,
        Name = node.Name,
        NodeType = node.IsDirectory ? ProgramFileNodeType.Directory : ProgramFileNodeType.File,
        SizeBytes = node.SizeBytes,
        ModifiedAt = node.ModifiedAt,
        CanDownload = node.CanDownload,
        CanUpload = node.CanUpload,
        HasChildren = node.HasChildren
    };

    private static async Task<IReadOnlyList<ProgramFileNodeDto>> BrowseCurrentLevelAsync(
        IAddressSpaceBrowser browser, string? path, CancellationToken ct)
    {
        if (browser is IProgramFileBrowser programBrowser)
        {
            var fileNodes = await programBrowser.BrowseFilesAsync(path, ct);
            return fileNodes.Select(MapProgramFile).ToArray();
        }

        var nodes = await browser.BrowseAsync(path, ct);
        return nodes.Select(MapNode).ToArray();
    }

    private static ProgramFileNodeDto MapNode(AddressNode node)
    {
        var isDirectory = node.NodeType == AddressNodeType.Folder;
        return new()
        {
            Path = node.Path,
            Name = GetNodeName(node.Path),
            NodeType = isDirectory ? ProgramFileNodeType.Directory : ProgramFileNodeType.File,
            SizeBytes = TryParseSize(node.DisplayName),
            ModifiedAt = null,
            CanDownload = !isDirectory,
            CanUpload = isDirectory && node.IsWritable,
            HasChildren = isDirectory
        };
    }

    private static string GetNodeName(string path)
    {
        var normalized = path.TrimEnd('/', '\\');
        var separatorIndex = Math.Max(normalized.LastIndexOf('/'), normalized.LastIndexOf('\\'));
        return separatorIndex >= 0 ? normalized[(separatorIndex + 1)..] : normalized;
    }

    private static long? TryParseSize(string displayName)
    {
        var match = SizePattern.Match(displayName.Trim());
        if (!match.Success || !double.TryParse(match.Groups["size"].Value, out var value)) return null;
        return match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "B" => (long)value,
            "KB" => (long)(value * 1024),
            "MB" => (long)(value * 1024 * 1024),
            _ => null
        };
    }
}
