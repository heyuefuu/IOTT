namespace IndustrialIoT.Protocols.Abstractions;

using IndustrialIoT.Protocols.Models;

public interface IProgramFileBrowser
{
    Task<IReadOnlyList<ProgramFileEntry>> BrowseFilesAsync(string? path = null, CancellationToken ct = default);
}
