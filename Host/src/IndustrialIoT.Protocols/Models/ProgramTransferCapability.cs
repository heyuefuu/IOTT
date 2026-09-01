namespace IndustrialIoT.Protocols.Models;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Abstractions;

public sealed record ProgramTransferCapability
{
    public required ProtocolType Protocol { get; init; }
    public required bool SupportsProgramTransfer { get; init; }
    public required bool SupportsBrowse { get; init; }
    public required bool SupportsResumeUpload { get; init; }
    public required bool SupportsFullRestartUpload { get; init; }
    public required string ResumeMode { get; init; }
    public string? Limitation { get; init; }

    public static ProgramTransferCapability FromDriver(ProtocolType protocol, IProtocolDriver driver)
    {
        var transfer = driver as INCProgramTransfer;
        var supportsResume = transfer?.SupportsResume == true;
        return new()
        {
            Protocol = protocol,
            SupportsProgramTransfer = transfer is not null,
            SupportsBrowse = driver is IAddressSpaceBrowser,
            SupportsResumeUpload = supportsResume,
            SupportsFullRestartUpload = transfer is not null,
            ResumeMode = supportsResume ? "ResumeUpload" : "FullRestartUpload",
            Limitation = transfer is null ? "Driver does not implement NC program transfer."
                : supportsResume ? null : "Protocol driver cannot resume partial uploads; retry from byte 0 is supported.",
        };
    }
}
