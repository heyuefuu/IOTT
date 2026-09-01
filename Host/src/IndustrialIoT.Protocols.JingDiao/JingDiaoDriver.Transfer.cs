namespace IndustrialIoT.Protocols.JingDiao;

using System.Diagnostics;
using IndustrialIoT.Protocols.Models;

public sealed partial class JingDiaoDriver
{
    public async Task<IReadOnlyList<ProgramFileEntry>> BrowseFilesAsync(string? path = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var result = await client!.BrowseFilesAsync(new JingDiaoBrowseFilesRequest(sessionId, path), ct);
        if (result.ReturnCode != 0 || result.Value is null) return [];
        return result.Value.Select(x => new ProgramFileEntry
        {
            Path = x.Path,
            Name = x.Name,
            IsDirectory = x.IsDirectory,
            SizeBytes = x.SizeBytes,
            CanDownload = !x.IsDirectory,
            CanUpload = x.IsDirectory,
            HasChildren = x.IsDirectory
        }).ToArray();
    }

    public async Task<TransferProgressResult> UploadProgramAsync(
        Stream source, NCProgramMetadata metadata,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var id = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        var totalBytes = metadata.FileSize ?? (source.CanSeek ? source.Length : 0);
        try
        {
            var fileName = string.IsNullOrWhiteSpace(metadata.FileName)
                ? Path.GetFileName(metadata.RemotePath)
                : metadata.FileName;
            var result = await client!.UploadAsync(sessionId, source, fileName,
                options?.UploadAddToTask ?? false, options?.UploadSetMainProgram ?? false, ct);
            sw.Stop();
            progress?.Report(new() { BytesTransferred = totalBytes, TotalBytes = totalBytes });
            return new()
            {
                Success = result.ReturnCode == 0,
                TransferId = id,
                BytesTransferred = result.ReturnCode == 0 ? totalBytes : 0,
                Duration = sw.Elapsed,
                ErrorMessage = result.ErrorMessage
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return FailedTransfer(id, sw.Elapsed, ex.Message);
        }
    }

    public async Task<TransferProgressResult> DownloadProgramAsync(
        string remotePath, Stream destination,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var id = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var stream = await client!.DownloadAsync(new JingDiaoDownloadRequest(sessionId, remotePath), ct);
            await stream.CopyToAsync(destination, ct);
            sw.Stop();
            var bytes = destination.CanSeek ? destination.Position : 0;
            progress?.Report(new() { BytesTransferred = bytes, TotalBytes = bytes });
            return new() { Success = true, TransferId = id, BytesTransferred = bytes, Duration = sw.Elapsed };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return FailedTransfer(id, sw.Elapsed, ex.Message);
        }
    }

    public Task<TransferProgressResult> ResumeUploadAsync(
        string transferId, string remotePath, Stream source, long offset,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
        => Task.FromResult(FailedTransfer(transferId, TimeSpan.Zero,
            "JingDiao SDK SendNcFile does not support resume upload."));

    private static TransferProgressResult FailedTransfer(string id, TimeSpan duration, string message) => new()
    {
        Success = false,
        TransferId = id,
        BytesTransferred = 0,
        Duration = duration,
        ErrorMessage = message
    };
}
