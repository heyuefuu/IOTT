namespace IndustrialIoT.Protocols.Abstractions;

/// <summary>
/// Verification-only, create-new file I/O. It never selects or executes an NC program. A returned
/// lease owns an atomically created object/namespace on this driver's captured connection. Failure
/// to acquire ownership MUST NOT open, overwrite, reuse, or delete an existing object.
/// </summary>
public interface IVerificationFileTransfer
{
    Task<IVerificationFileLease> CreateVerificationFileAsync(
        string directory, string fileName, CancellationToken ct);
}

public interface IVerificationFileLease
{
    string RemotePath { get; }
    Task<long> UploadAsync(Stream source, long length, CancellationToken ct);
    Task DownloadAsync(Stream destination, CancellationToken ct);

    // Delete only the owned file and, if applicable, its empty exclusive directory; never recurse.
    // The caller supplies a bounded token independent of the possibly cancelled request.
    Task CleanupAsync(CancellationToken ct);
}
