namespace IndustrialIoT.Protocols.Abstractions;

using IndustrialIoT.Protocols.Models;

public interface INCProgramTransfer
{
    bool SupportsResume { get; }
    Task<TransferProgressResult> UploadProgramAsync(Stream source, NCProgramMetadata metadata, IProgress<TransferProgress>? progress = null, CancellationToken ct = default);
    Task<TransferProgressResult> DownloadProgramAsync(string remotePath, Stream destination, IProgress<TransferProgress>? progress = null, CancellationToken ct = default);
    Task<TransferProgressResult> ResumeUploadAsync(string transferId, string remotePath, Stream source, long offset, IProgress<TransferProgress>? progress = null, CancellationToken ct = default);
}
