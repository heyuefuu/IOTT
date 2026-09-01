namespace IndustrialIoT.Protocols.FileTransfer;

using System.Diagnostics;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using SMBLibrary;
using SMBLibrary.Client;
using ConnectionState = IndustrialIoT.Domain.Enums.ConnectionState;

[ProtocolDriver(ProtocolType.SMB, "SMB", "*")]
public class SmbTransferDriver : IProtocolDriver, INCProgramTransfer, IAddressSpaceBrowser
{
    private readonly ILogger<SmbTransferDriver> _logger;
    private SMB2Client? _smbClient;
    private ISMBFileStore? _fileStore;
    private ConnectionState _state = ConnectionState.Disconnected;
    private DeviceConnectionConfig? _config;
    private string _shareName = "";

    private const int BufferSize = 65536; // 64KB chunks

    public ProtocolType Protocol => ProtocolType.SMB;
    public ConnectionState State => _state;
    public DriverCapabilities Capabilities =>
        DriverCapabilities.FileTransfer | DriverCapabilities.Browse;
    public bool SupportsResume => true;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public SmbTransferDriver(ILogger<SmbTransferDriver> logger)
    {
        _logger = logger;
    }

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        TransitionState(ConnectionState.Connecting);
        _config = config;

        return await Task.Run(() =>
        {
            try
            {
                _smbClient = new SMB2Client();
                var connected = _smbClient.Connect(config.Host, SMBTransportType.DirectTCPTransport);
                if (!connected)
                {
                    TransitionState(ConnectionState.Faulted, "SMB TCP connection failed");
                    return new ConnectionResult { Success = false, ErrorMessage = $"Cannot connect to SMB server at {config.Host}" };
                }

                // NTLM login or guest
                NTStatus loginStatus;
                if (!string.IsNullOrEmpty(config.Username))
                {
                    // Extract domain from username if present (DOMAIN\user or user@domain)
                    var (domain, username) = ParseDomainUsername(config.Username);
                    loginStatus = _smbClient.Login(domain, username, config.Password ?? "");
                }
                else
                {
                    loginStatus = _smbClient.Login("", "", "");
                }

                if (loginStatus != NTStatus.STATUS_SUCCESS)
                {
                    TransitionState(ConnectionState.Faulted, $"SMB login failed: {loginStatus}");
                    return new ConnectionResult { Success = false, ErrorMessage = $"SMB login failed: {loginStatus}" };
                }

                // Get share name from extended properties
                _shareName = config.ExtendedProperties.GetValueOrDefault("ShareName", "");
                if (string.IsNullOrWhiteSpace(_shareName))
                {
                    TransitionState(ConnectionState.Faulted, "ShareName not specified");
                    return new ConnectionResult { Success = false, ErrorMessage = "ExtendedProperties must contain 'ShareName'" };
                }

                _fileStore = _smbClient.TreeConnect(_shareName, out var treeStatus);
                if (treeStatus != NTStatus.STATUS_SUCCESS || _fileStore is null)
                {
                    TransitionState(ConnectionState.Faulted, $"Tree connect failed: {treeStatus}");
                    return new ConnectionResult { Success = false, ErrorMessage = $"Failed to connect to share '{_shareName}': {treeStatus}" };
                }

                TransitionState(ConnectionState.Connected);
                _logger.LogInformation("SMB connected to {Host}\\{Share}", config.Host, _shareName);
                return new ConnectionResult { Success = true };
            }
            catch (Exception ex)
            {
                TransitionState(ConnectionState.Faulted, ex.Message);
                _logger.LogError(ex, "SMB connection failed to {Host}", config.Host);
                return new ConnectionResult { Success = false, ErrorMessage = ex.Message };
            }
        }, ct);
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        DisconnectInternal();
        TransitionState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task<bool> PingAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_state == ConnectionState.Connected && _smbClient?.IsConnected == true);
    }

    public Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
        => throw new NotSupportedException("SMB driver does not support tag read operations. Use file transfer methods.");

    public Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
        => throw new NotSupportedException("SMB driver does not support tag read operations. Use file transfer methods.");

    public Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
        => throw new NotSupportedException("SMB driver does not support tag write operations. Use file transfer methods.");

    public async Task<TransferProgressResult> UploadProgramAsync(
        Stream source, NCProgramMetadata metadata,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var transferId = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();

        return await Task.Run(() =>
        {
            try
            {
                var remotePath = FileTransferRemotePath.CombineSmbDirectory(metadata.RemotePath, metadata.FileName);
                var totalBytes = metadata.FileSize ?? source.Length;

                _logger.LogInformation("SMB upload started: {RemotePath} ({TotalBytes} bytes)", remotePath, totalBytes);

                EnsureDirectoryExists(remotePath);

                var createStatus = _fileStore!.CreateFile(
                    out var fileHandle, out _,
                    remotePath,
                    AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE,
                    SMBLibrary.FileAttributes.Normal,
                    ShareAccess.None,
                    CreateDisposition.FILE_OVERWRITE_IF,
                    CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
                    null);

                if (createStatus != NTStatus.STATUS_SUCCESS)
                    return CreateFailedResult(transferId, sw, $"Failed to create remote file: {createStatus}");

                try
                {
                    long transferred = 0;
                    var buffer = new byte[BufferSize];
                    int read;

                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();

                        var data = buffer.Length == read ? buffer : buffer[..read];
                        var writeStatus = _fileStore.WriteFile(out _, fileHandle, transferred, data);
                        if (writeStatus != NTStatus.STATUS_SUCCESS)
                            return CreateFailedResult(transferId, sw, $"SMB write failed at offset {transferred}: {writeStatus}");

                        transferred += read;
                        progress?.Report(new TransferProgress { BytesTransferred = transferred, TotalBytes = totalBytes });
                    }

                    sw.Stop();
                    _logger.LogInformation("SMB upload completed: {RemotePath} ({Bytes} bytes) in {Duration}", remotePath, transferred, sw.Elapsed);

                    return new TransferProgressResult
                    {
                        Success = true,
                        TransferId = transferId,
                        BytesTransferred = transferred,
                        Duration = sw.Elapsed
                    };
                }
                finally
                {
                    _fileStore.CloseFile(fileHandle);
                }
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return CreateFailedResult(transferId, sw, "Upload cancelled");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "SMB upload failed for {FileName}", metadata.FileName);
                return CreateFailedResult(transferId, sw, ex.Message);
            }
        }, ct);
    }

    public async Task<TransferProgressResult> DownloadProgramAsync(
        string remotePath, Stream destination,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var transferId = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();

        return await Task.Run(() =>
        {
            try
            {
                var normalizedPath = FileTransferRemotePath.NormalizeSmbPath(remotePath);

                _logger.LogInformation("SMB download started: {RemotePath}", normalizedPath);

                var createStatus = _fileStore!.CreateFile(
                    out var fileHandle, out _,
                    normalizedPath,
                    AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                    SMBLibrary.FileAttributes.Normal,
                    ShareAccess.Read,
                    CreateDisposition.FILE_OPEN,
                    CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
                    null);

                if (createStatus != NTStatus.STATUS_SUCCESS)
                    return CreateFailedResult(transferId, sw, $"Failed to open remote file: {createStatus}");

                try
                {
                    // Get file size
                    var totalBytes = GetFileSize(fileHandle);
                    long transferred = 0;

                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();

                        var readStatus = _fileStore.ReadFile(out var data, fileHandle, transferred, BufferSize);
                        if (readStatus == NTStatus.STATUS_END_OF_FILE || data is null || data.Length == 0)
                            break;

                        if (readStatus != NTStatus.STATUS_SUCCESS)
                            return CreateFailedResult(transferId, sw, $"SMB read failed at offset {transferred}: {readStatus}");

                        destination.Write(data, 0, data.Length);
                        transferred += data.Length;
                        progress?.Report(new TransferProgress { BytesTransferred = transferred, TotalBytes = totalBytes });
                    }

                    sw.Stop();
                    _logger.LogInformation("SMB download completed: {RemotePath} ({Bytes} bytes) in {Duration}", normalizedPath, transferred, sw.Elapsed);

                    return new TransferProgressResult
                    {
                        Success = true,
                        TransferId = transferId,
                        BytesTransferred = transferred,
                        Duration = sw.Elapsed
                    };
                }
                finally
                {
                    _fileStore.CloseFile(fileHandle);
                }
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return CreateFailedResult(transferId, sw, "Download cancelled");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "SMB download failed for {RemotePath}", remotePath);
                return CreateFailedResult(transferId, sw, ex.Message);
            }
        }, ct);
    }

    public async Task<TransferProgressResult> ResumeUploadAsync(
        string transferId, string remotePath, Stream source, long offset,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var sw = Stopwatch.StartNew();

        return await Task.Run(() =>
        {
            try
            {
                // Seek source to offset
                if (source.CanSeek)
                    source.Seek(offset, SeekOrigin.Begin);

                var totalBytes = source.CanSeek ? source.Length : offset;

                var createStatus = _fileStore!.CreateFile(
                    out var fileHandle, out _,
                    FileTransferRemotePath.NormalizeSmbPath(remotePath),
                    AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE,
                    SMBLibrary.FileAttributes.Normal,
                    ShareAccess.None,
                    CreateDisposition.FILE_OPEN_IF,
                    CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
                    null);

                if (createStatus != NTStatus.STATUS_SUCCESS)
                    return CreateFailedResult(transferId, sw, $"Failed to open file for resume: {createStatus}");

                try
                {
                    long writeOffset = offset;
                    var buffer = new byte[BufferSize];
                    int read;

                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();

                        var data = buffer.Length == read ? buffer : buffer[..read];
                        var writeStatus = _fileStore.WriteFile(out _, fileHandle, writeOffset, data);
                        if (writeStatus != NTStatus.STATUS_SUCCESS)
                            return CreateFailedResult(transferId, sw, $"SMB resume write failed at offset {writeOffset}: {writeStatus}");

                        writeOffset += read;
                        progress?.Report(new TransferProgress { BytesTransferred = writeOffset, TotalBytes = totalBytes });
                    }

                    sw.Stop();
                    return new TransferProgressResult
                    {
                        Success = true,
                        TransferId = transferId,
                        BytesTransferred = writeOffset,
                        Duration = sw.Elapsed
                    };
                }
                finally
                {
                    _fileStore.CloseFile(fileHandle);
                }
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return CreateFailedResult(transferId, sw, "Resume upload cancelled");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "SMB resume upload failed for transfer {TransferId}", transferId);
                return CreateFailedResult(transferId, sw, ex.Message);
            }
        }, ct);
    }

    public async Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        EnsureConnected();

        return await Task.Run(() =>
        {
            var searchPath = string.IsNullOrEmpty(parentPath) ? "*" : $@"{FileTransferRemotePath.NormalizeSmbPath(parentPath).TrimEnd('\\')}\*";

            var status = _fileStore!.CreateFile(
                out var dirHandle, out _,
                string.IsNullOrEmpty(parentPath) ? "" : FileTransferRemotePath.NormalizeSmbPath(parentPath),
                AccessMask.GENERIC_READ,
                SMBLibrary.FileAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write,
                CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_DIRECTORY_FILE,
                null);

            if (status != NTStatus.STATUS_SUCCESS)
            {
                _logger.LogWarning("Failed to open directory for browsing: {Path} ({Status})", parentPath, status);
                return Array.Empty<AddressNode>() as IReadOnlyList<AddressNode>;
            }

            try
            {
                var nodes = new List<AddressNode>();
                var queryStatus = _fileStore.QueryDirectory(
                    out var entries, dirHandle, "*",
                    FileInformationClass.FileDirectoryInformation);

                if ((queryStatus == NTStatus.STATUS_SUCCESS ||
                     queryStatus == NTStatus.STATUS_NO_MORE_FILES) &&
                    entries is not null)
                {
                    foreach (var entry in entries)
                    {
                        if (entry is FileDirectoryInformation fileInfo)
                        {
                            var name = fileInfo.FileName;
                            if (name is "." or "..") continue;

                            var isDir = (fileInfo.FileAttributes & SMBLibrary.FileAttributes.Directory) != 0;
                            var fullPath = string.IsNullOrEmpty(parentPath)
                                ? name
                                : $@"{parentPath.TrimEnd('/', '\\')}\{name}";

                            nodes.Add(new AddressNode
                            {
                                Path = fullPath,
                                DisplayName = name,
                                NodeType = isDir ? AddressNodeType.Folder : AddressNodeType.Variable,
                                DataType = isDir ? null : DataType.ByteArray,
                                IsReadable = true,
                                IsWritable = (fileInfo.FileAttributes & SMBLibrary.FileAttributes.ReadOnly) == 0
                            });
                        }
                    }
                }

                return nodes as IReadOnlyList<AddressNode>;
            }
            finally
            {
                _fileStore.CloseFile(dirHandle);
            }
        }, ct);
    }

    public Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
        => throw new NotSupportedException("SMB driver does not support address space export.");

    public ValueTask DisposeAsync()
    {
        DisconnectInternal();
        if (_state != ConnectionState.Disconnected)
            TransitionState(ConnectionState.Disconnected);
        return ValueTask.CompletedTask;
    }

    // === Private helpers ===

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected || _fileStore is null || _smbClient?.IsConnected != true)
            throw new InvalidOperationException("SMB client is not connected. Call ConnectAsync first.");
    }

    private void TransitionState(ConnectionState newState, string? reason = null)
    {
        var old = _state;
        if (old == newState) return;
        _state = newState;
        StateChanged?.Invoke(this, new() { OldState = old, NewState = newState, Reason = reason });
    }

    private static (string domain, string username) ParseDomainUsername(string input)
    {
        // Handle DOMAIN\user format
        if (input.Contains('\\'))
        {
            var parts = input.Split('\\', 2);
            return (parts[0], parts[1]);
        }

        // Handle user@domain format
        if (input.Contains('@'))
        {
            var parts = input.Split('@', 2);
            return (parts[1], parts[0]);
        }

        return ("", input);
    }

    private long GetFileSize(object fileHandle)
    {
        try
        {
            var status = _fileStore!.GetFileInformation(
                out var fileInfo, fileHandle,
                FileInformationClass.FileStandardInformation);

            if (status == NTStatus.STATUS_SUCCESS && fileInfo is FileStandardInformation stdInfo)
                return stdInfo.EndOfFile;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get file size from SMB handle");
        }

        return 0;
    }

    private void EnsureDirectoryExists(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath)?.Replace('/', '\\');
        if (string.IsNullOrEmpty(dir)) return;

        var parts = dir.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var current = "";

        foreach (var part in parts)
        {
            current = string.IsNullOrEmpty(current) ? part : $@"{current}\{part}";

            var status = _fileStore!.CreateFile(
                out var dirHandle, out _,
                current,
                AccessMask.GENERIC_READ,
                SMBLibrary.FileAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write,
                CreateDisposition.FILE_OPEN_IF,
                CreateOptions.FILE_DIRECTORY_FILE,
                null);

            if (status == NTStatus.STATUS_SUCCESS)
                _fileStore.CloseFile(dirHandle);
        }
    }

    private void DisconnectInternal()
    {
        try
        {
            if (_fileStore is not null)
            {
                _fileStore.Disconnect();
                _fileStore = null;
            }

            if (_smbClient is not null)
            {
                _smbClient.Logoff();
                _smbClient.Disconnect();
                _smbClient = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during SMB disconnect");
        }
    }

    private static TransferProgressResult CreateFailedResult(string transferId, Stopwatch sw, string error)
    {
        sw.Stop();
        return new TransferProgressResult
        {
            Success = false,
            TransferId = transferId,
            BytesTransferred = 0,
            Duration = sw.Elapsed,
            ErrorMessage = error
        };
    }
}
