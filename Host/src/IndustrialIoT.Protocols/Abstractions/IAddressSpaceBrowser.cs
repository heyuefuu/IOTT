namespace IndustrialIoT.Protocols.Abstractions;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

public interface IAddressSpaceBrowser
{
    Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default);
    Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default);
}
