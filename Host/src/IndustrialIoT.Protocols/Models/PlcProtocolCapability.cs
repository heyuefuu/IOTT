namespace IndustrialIoT.Protocols.Models;

public sealed record PlcProtocolCapability
{
    public required string Brand { get; init; }
    public required IReadOnlyList<string> Models { get; init; }
    public required IReadOnlyList<string> Protocols { get; init; }
    public bool SupportsRead { get; init; } = true;
    public bool SupportsWrite { get; init; } = true;
    public bool SupportsBrowse { get; init; } = true;
    public bool SupportsExport { get; init; } = true;
    public bool SupportsBatchImport { get; init; } = true;
    public bool SupportsSampling { get; init; } = true;
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> RequiredProperties { get; init; }
    public required IReadOnlyList<string> AddressExamples { get; init; }
}
