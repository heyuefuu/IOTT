namespace IndustrialIoT.Host.Services;

using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;

public interface IAddressSpaceBrowseService
{
    Task<IReadOnlyList<AddressNode>> BrowseAsync(
        IAddressSpaceBrowser browser,
        string? path,
        bool recursive = false,
        CancellationToken ct = default);
}

public sealed class AddressSpaceBrowseService : IAddressSpaceBrowseService
{
    public Task<IReadOnlyList<AddressNode>> BrowseAsync(
        IAddressSpaceBrowser browser,
        string? path,
        bool recursive = false,
        CancellationToken ct = default)
        => recursive ? BrowseRecursiveAsync(browser, path, ct) : browser.BrowseAsync(path, ct);

    private async Task<IReadOnlyList<AddressNode>> BrowseRecursiveAsync(
        IAddressSpaceBrowser browser, string? path, CancellationToken ct)
    {
        var nodes = new List<AddressNode>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await BrowseCoreAsync(browser, path, nodes, visited, ct);
        return nodes;
    }

    private static async Task BrowseCoreAsync(
        IAddressSpaceBrowser browser, string? path, List<AddressNode> nodes, HashSet<string> visited, CancellationToken ct)
    {
        foreach (var node in await browser.BrowseAsync(path, ct))
        {
            if (!visited.Add(node.Path)) continue;
            nodes.Add(node with { Children = null });
            if (node.NodeType == AddressNodeType.Folder)
                await BrowseCoreAsync(browser, node.Path, nodes, visited, ct);
        }
    }
}
