namespace IndustrialIoT.Protocols.NCLink;

using System.Text.Json.Nodes;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;

public sealed partial class NCLinkDriver : IProgramFileBrowser
{
    public async Task<IReadOnlyList<ProgramFileEntry>> BrowseFilesAsync(string? path = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var directory = NormalizeBrowsePath(path);
        var mid = NCLinkProtocol.NextMessageId();
        var payload = NCLinkProtocol.BuildFileListRequest(mid, ResolveFileDataItemId(), directory);
        var resp = await PublishAndWaitAsync(NCLinkProtocol.SetRequestTopic(_deviceGuid), payload, mid, ct);
        NCLinkProtocol.ThrowIfError(resp);
        return ParseFileEntries(resp.Raw, directory);
    }

    private static IReadOnlyList<ProgramFileEntry> ParseFileEntries(JsonObject raw, string directory)
    {
        var value = raw["values"]?.AsArray()?.FirstOrDefault()?["value"];
        var paths = value is JsonArray items
            ? items.Where(x => x is not null).Select(x => ToAbsolutePath(x!, directory)).ToArray()
            : Array.Empty<string>();
        var result = new List<ProgramFileEntry>();
        foreach (var group in paths.Where(x => !string.IsNullOrEmpty(x)).GroupBy(x => GetChildPath(directory, x)))
        {
            var child = group.Key;
            if (string.IsNullOrEmpty(child)) continue;
            var isDirectory = group.Any(x => !string.Equals(x, child, StringComparison.OrdinalIgnoreCase));
            result.Add(new ProgramFileEntry
            {
                Path = child,
                Name = child[(child.LastIndexOf('/') + 1)..],
                IsDirectory = isDirectory,
                SizeBytes = null,
                ModifiedAt = null,
                CanDownload = !isDirectory,
                CanUpload = isDirectory,
                HasChildren = isDirectory,
            });
        }
        return result.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string NormalizeBrowsePath(string? path) => string.IsNullOrWhiteSpace(path) ? "/" : "/" + path.Replace('\\', '/').Trim().Trim('/'); 
    private static string ToAbsolutePath(JsonNode node, string directory) => NormalizeBrowsePath(node.GetValue<string>().StartsWith('/') ? node.GetValue<string>() : $"{directory}/{node.GetValue<string>()}");
    private static string GetChildPath(string directory, string fullPath)
    {
        if (directory == "/") return "/" + fullPath.Trim('/').Split('/', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        if (!fullPath.StartsWith(directory.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)) return "";
        var tail = fullPath[(directory.TrimEnd('/').Length + 1)..];
        var child = tail.Split('/', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return string.IsNullOrEmpty(child) ? "" : $"{directory.TrimEnd('/')}/{child}";
    }
}
