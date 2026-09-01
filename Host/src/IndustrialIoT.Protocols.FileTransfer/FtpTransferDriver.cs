namespace IndustrialIoT.Protocols.FileTransfer;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluentFTP;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;

[ProtocolDriver(ProtocolType.FTP, "FTP", "*")]
public class FtpTransferDriver : IProtocolDriver, INCProgramTransfer, IAddressSpaceBrowser
{
    private readonly ILogger<FtpTransferDriver> _logger;
    private AsyncFtpClient? _client;
    private ConnectionState _state = ConnectionState.Disconnected;
    private DeviceConnectionConfig? _config;

    private const int BufferSize = 81920; // 80KB chunks

    public ProtocolType Protocol => ProtocolType.FTP;
    public ConnectionState State => _state;
    public DriverCapabilities Capabilities => DriverCapabilities.FileTransfer;
    public bool SupportsResume => true;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public FtpTransferDriver(ILogger<FtpTransferDriver> logger)
    {
        _logger = logger;
    }

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        TransitionState(ConnectionState.Connecting);
        _config = config;

        try
        {
            _client = new AsyncFtpClient(config.Host, config.Port)
            {
                Config =
                {
                    ConnectTimeout = (int)config.ConnectTimeout.TotalMilliseconds,
                    ReadTimeout = (int)config.ReadTimeout.TotalMilliseconds,
                    DataConnectionConnectTimeout = (int)config.ConnectTimeout.TotalMilliseconds,
                    DataConnectionReadTimeout = (int)config.ReadTimeout.TotalMilliseconds,
                }
            };
            ApplyFtpOptions(_client.Config, config.ExtendedProperties);

            // Anonymous auth when username is null/empty
            if (!string.IsNullOrEmpty(config.Username))
            {
                _client.Credentials = new System.Net.NetworkCredential(config.Username, config.Password ?? "");
            }
            else
            {
                _client.Credentials = new System.Net.NetworkCredential("anonymous", "anonymous@");
            }

            await _client.Connect(ct);

            TransitionState(ConnectionState.Connected);
            _logger.LogInformation("FTP connected to {Host}:{Port}", config.Host, config.Port);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            TransitionState(ConnectionState.Faulted, ex.Message);
            _logger.LogError(ex, "FTP connection failed to {Host}:{Port}", config.Host, config.Port);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_client is not null)
        {
            try
            {
                await _client.Disconnect(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during FTP disconnect");
            }
        }

        TransitionState(ConnectionState.Disconnected);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_client is null || _state != ConnectionState.Connected)
            return false;

        try
        {
            return _client.IsConnected && await _client.GetWorkingDirectory(ct) is not null;
        }
        catch
        {
            return false;
        }
    }

    public Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
        => throw new NotSupportedException("FTP driver does not support tag read operations. Use UploadProgramAsync/DownloadProgramAsync for file transfers.");

    public Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
        => throw new NotSupportedException("FTP driver does not support tag read operations. Use UploadProgramAsync/DownloadProgramAsync for file transfers.");

    public Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
        => throw new NotSupportedException("FTP driver does not support tag write operations. Use UploadProgramAsync/DownloadProgramAsync for file transfers.");

    public async Task<TransferProgressResult> UploadProgramAsync(
        Stream source, NCProgramMetadata metadata,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var transferId = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        var remotePath = FileTransferRemotePath.CombineFtpDirectory(metadata.RemotePath, metadata.FileName);
        var totalBytes = metadata.FileSize ?? source.Length;

        try
        {
            _logger.LogInformation("FTP upload started: {RemotePath} ({TotalBytes} bytes)", remotePath, totalBytes);

            // FluentFTP handles upload with progress natively
            var status = await _client!.UploadStream(source, remotePath, FtpRemoteExists.Overwrite,
                createRemoteDir: true, token: ct,
                progress: CreateFtpProgress(totalBytes, progress));
            if (status != FtpStatus.Success)
                throw new InvalidOperationException($"FTP upload failed with status {status}.");

            sw.Stop();
            var checksum = await ComputeChecksumAsync(source, ct);
            _logger.LogInformation("FTP upload completed: {RemotePath} in {Duration}", remotePath, sw.Elapsed);

            return new()
            {
                Success = true,
                TransferId = transferId,
                BytesTransferred = totalBytes,
                Duration = sw.Elapsed,
                Checksum = checksum
            };
        }
        catch (Exception ex)
        {
            if (RequiresNoCheckFallback(ex) && source.CanSeek)
            {
                _logger.LogWarning(ex, "FTP upload retrying without remote listing for {RemotePath}", remotePath);
                try
                {
                    source.Seek(0, SeekOrigin.Begin);
                    try { await _client!.DeleteFile(remotePath, ct); } catch { }
                    var retryStatus = await _client!.UploadStream(source, remotePath, FtpRemoteExists.NoCheck,
                        createRemoteDir: true, token: ct,
                        progress: CreateFtpProgress(totalBytes, progress));
                    if (retryStatus != FtpStatus.Success)
                        throw new InvalidOperationException($"FTP upload retry failed with status {retryStatus}.");
                    sw.Stop();
                    return new()
                    {
                        Success = true,
                        TransferId = transferId,
                        BytesTransferred = totalBytes,
                        Duration = sw.Elapsed,
                        Checksum = await ComputeChecksumAsync(source, ct)
                    };
                }
                catch (Exception retryEx) { ex = retryEx; }
            }
            sw.Stop();
            _logger.LogError(ex, "FTP upload failed for {FileName}", metadata.FileName);
            return new()
            {
                Success = false,
                TransferId = transferId,
                BytesTransferred = 0,
                Duration = sw.Elapsed,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<TransferProgressResult> DownloadProgramAsync(
        string remotePath, Stream destination,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var transferId = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();

        try
        {
            // Get remote file size for progress
            var fileSize = await _client!.GetFileSize(remotePath, -1, ct);
            var totalBytes = fileSize > 0 ? fileSize : 0L;

            _logger.LogInformation("FTP download started: {RemotePath} ({TotalBytes} bytes)", remotePath, totalBytes);

            var downloaded = await _client.DownloadStream(destination, remotePath, token: ct,
                progress: totalBytes > 0 ? CreateFtpProgress(totalBytes, progress) : null);
            if (!downloaded)
                throw new InvalidOperationException("FTP download failed.");

            sw.Stop();
            var bytesTransferred = destination.Position;
            if (totalBytes > 0 && bytesTransferred < totalBytes)
                throw new IOException($"FTP download incomplete: {bytesTransferred}/{totalBytes} bytes transferred.");
            _logger.LogInformation("FTP download completed: {RemotePath} ({Bytes} bytes) in {Duration}", remotePath, bytesTransferred, sw.Elapsed);

            return new()
            {
                Success = true,
                TransferId = transferId,
                BytesTransferred = bytesTransferred,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "FTP download failed for {RemotePath}", remotePath);
            return new()
            {
                Success = false,
                TransferId = transferId,
                BytesTransferred = 0,
                Duration = sw.Elapsed,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<TransferProgressResult> ResumeUploadAsync(
        string transferId, string remotePath, Stream source, long offset,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var sw = Stopwatch.StartNew();

        try
        {
            // Seek source to the offset position
            if (source.CanSeek)
                source.Seek(offset, SeekOrigin.Begin);

            var totalBytes = source.CanSeek ? source.Length : offset;

            _logger.LogInformation("FTP resume upload: {RemotePath} from offset {Offset}", remotePath, offset);

            // FluentFTP natively supports REST command for append/resume
            await _client!.UploadStream(source, remotePath, FtpRemoteExists.Resume,
                createRemoteDir: true, token: ct,
                progress: CreateFtpProgress(totalBytes, progress));

            sw.Stop();
            var bytesTransferred = source.CanSeek ? source.Position : totalBytes;

            return new()
            {
                Success = true,
                TransferId = transferId,
                BytesTransferred = bytesTransferred,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "FTP resume upload failed for transfer {TransferId}", transferId);
            return new()
            {
                Success = false,
                TransferId = transferId,
                BytesTransferred = offset,
                Duration = sw.Elapsed,
                ErrorMessage = ex.Message
            };
        }
    }

    // ── Directory Browsing (IAddressSpaceBrowser) ──

    public async Task<IReadOnlyList<AddressNode>> BrowseAsync(
        string? parentPath = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var remotePath = string.IsNullOrEmpty(parentPath) ? "/" : parentPath;
        var listing = await _client!.GetListing(remotePath, FtpListOption.Auto, ct);

        var nodes = new List<AddressNode>();
        foreach (var item in listing)
        {
            nodes.Add(new AddressNode
            {
                Path = item.FullName,
                DisplayName = $"{item.Name}{(item.Type == FtpObjectType.Directory ? "/" : "")} ({FormatSize(item.Size)})",
                NodeType = item.Type == FtpObjectType.Directory
                    ? AddressNodeType.Folder
                    : AddressNodeType.Variable,
                DataType = item.Type == FtpObjectType.File ? DataType.String : null,
                IsReadable = true,
                IsWritable = true,
            });
        }
        return nodes;
    }

    public async Task<Stream> ExportAddressSpaceAsync(
        ExportFormat format, CancellationToken ct = default)
    {
        EnsureConnected();
        var sb = new StringBuilder();
        sb.AppendLine("Path,Name,Type,Size,Modified");
        await ExportDirectoryRecursive(sb, "/", 0, ct);
        Stream stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        return stream;
    }

    private async Task ExportDirectoryRecursive(
        StringBuilder sb, string path, int depth, CancellationToken ct)
    {
        if (depth > 10) return;
        var listing = await _client!.GetListing(path, FtpListOption.Auto, ct);
        foreach (var item in listing)
        {
            sb.AppendLine(
                $"\"{item.FullName}\",\"{item.Name}\",{item.Type},{item.Size},{item.Modified:O}");
            if (item.Type == FtpObjectType.Directory)
                await ExportDirectoryRecursive(sb, item.FullName, depth + 1, ct);
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1}MB"
    };

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            try
            {
                if (_client.IsConnected)
                    await _client.Disconnect();
            }
            catch { /* best-effort cleanup */ }

            _client.Dispose();
            _client = null;
        }

        if (_state != ConnectionState.Disconnected)
            TransitionState(ConnectionState.Disconnected);
    }

    // === Private helpers ===

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected || _client is null || !_client.IsConnected)
            throw new InvalidOperationException("FTP client is not connected. Call ConnectAsync first.");
    }

    private void TransitionState(ConnectionState newState, string? reason = null)
    {
        var old = _state;
        if (old == newState) return;
        _state = newState;
        StateChanged?.Invoke(this, new() { OldState = old, NewState = newState, Reason = reason });
    }

    private static bool RequiresNoCheckFallback(Exception ex) =>
        ex.ToString().Contains("Command[NLST", StringComparison.OrdinalIgnoreCase) &&
        ex.ToString().Contains("not implemented for PASV", StringComparison.OrdinalIgnoreCase);

    private static void ApplyFtpOptions(FtpConfig ftpConfig, IReadOnlyDictionary<string, string> properties)
    {
        var encryptionMode = GetOption(properties, "EncryptionMode", "FtpsMode", "FtpEncryptionMode");
        if (!string.IsNullOrWhiteSpace(encryptionMode) &&
            Enum.TryParse<FtpEncryptionMode>(encryptionMode, ignoreCase: true, out var parsedMode))
        {
            ftpConfig.EncryptionMode = parsedMode;
        }

        if (TryGetBool(properties, out var dataConnectionEncryption, "DataConnectionEncryption", "EncryptDataConnection"))
            ftpConfig.DataConnectionEncryption = dataConnectionEncryption;

        if (TryGetBool(properties, out var validateAnyCertificate, "ValidateAnyCertificate", "TrustAnyCertificate"))
            ftpConfig.ValidateAnyCertificate = validateAnyCertificate;
    }

    private static string? GetOption(IReadOnlyDictionary<string, string> properties, params string[] keys)
    {
        foreach (var key in keys)
            if (properties.TryGetValue(key, out var value))
                return value;
        return null;
    }

    private static bool TryGetBool(IReadOnlyDictionary<string, string> properties, out bool value, params string[] keys)
    {
        value = false;
        var raw = GetOption(properties, keys);
        return !string.IsNullOrWhiteSpace(raw) && bool.TryParse(raw, out value);
    }

    private static IProgress<FtpProgress>? CreateFtpProgress(long totalBytes, IProgress<TransferProgress>? progress)
    {
        if (progress is null) return null;

        return new Progress<FtpProgress>(p =>
        {
            var transferred = (long)(p.Progress / 100.0 * totalBytes);
            progress.Report(new TransferProgress
            {
                BytesTransferred = transferred,
                TotalBytes = totalBytes
            });
        });
    }

    private static async Task<string?> ComputeChecksumAsync(Stream stream, CancellationToken ct)
    {
        if (!stream.CanSeek) return null;

        stream.Seek(0, SeekOrigin.Begin);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

}
