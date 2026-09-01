namespace IndustrialIoT.Protocols.FANUC;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;

/// <summary>
/// FANUC FOCAS2 protocol driver for CNC controllers.
/// Communicates via the FOCAS2 API (native DLL interop abstracted through <see cref="IFocasApi"/>).
/// Supports FANUC 0i/30i-class CNCs such as 0i-MF, 0i-D, 30i/31i/32i.
/// </summary>
[ProtocolDriver(ProtocolType.FOCAS, "FANUC", "发那科", "0i-MF", "0i-D", "30i", "31i", "32i")]
public sealed class FocasDriver : IProtocolDriver, IAddressSpaceBrowser, IProgramFileBrowser, INCProgramTransfer
{
    private readonly ILogger<FocasDriver> _logger;
    private readonly IFocasApi _api;
    private readonly FocasAddressMapper _mapper;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private DeviceConnectionConfig? _config;
    private int _handle;
    private ConnectionState _state = ConnectionState.Disconnected;

    private const int DefaultPort = 8193;       // FOCAS2 default Ethernet port
    private const int DefaultTimeoutMs = 10_000;
    private const int ProgramChunkSize = 256;
    private const string CncMemoryRoot = "//CNC_MEM";
    private const string CncMemoryBrowseRootsKey = "CncMemoryBrowseRoots";

    public ProtocolType Protocol => ProtocolType.FOCAS;
    public ConnectionState State => _state;
    public bool SupportsResume => false;

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Read | DriverCapabilities.Write |
        DriverCapabilities.Browse | DriverCapabilities.BatchRead;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public FocasDriver(ILogger<FocasDriver> logger, IFocasApi? api = null)
    {
        _logger = logger;
        _api = api ?? new NativeFocasApi();
        _mapper = new FocasAddressMapper(_api);
    }

    // ───────────────────────── Connection ─────────────────────────

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            if (_state == ConnectionState.Connected)
                return new() { Success = true };

            SetState(ConnectionState.Connecting);
            _config = config;

            int port = config.Port > 0 ? config.Port : DefaultPort;
            int timeout = config.ConnectTimeout.TotalMilliseconds > 0
                ? (int)config.ConnectTimeout.TotalMilliseconds
                : DefaultTimeoutMs;

            _logger.LogInformation("Connecting to FANUC FOCAS at {Host}:{Port} (timeout {Timeout}ms)...",
                config.Host, port, timeout);

            // FOCAS2 connect is blocking — offload, then enforce a managed timeout.
            var connectTask = Task.Run(() =>
            {
                int handle = 0;
                int rc = _api.Connect(config.Host, port, timeout, out handle);
                return (rc, handle);
            });

            (int rc, int handle) connectResult;
            try
            {
                connectResult = await connectTask.WaitAsync(TimeSpan.FromMilliseconds(timeout), ct);
            }
            catch (TimeoutException)
            {
                TrackLateConnectCleanup(connectTask, config.Host, port);
                var message = $"FOCAS connect timed out after {timeout}ms";
                SetState(ConnectionState.Faulted, message);
                _logger.LogError("FOCAS connect timed out for {Host}:{Port} after {Timeout}ms",
                    config.Host, port, timeout);
                return new() { Success = false, ErrorMessage = message };
            }

            _handle = connectResult.handle;

            if (connectResult.rc != 0)
            {
                var error = FormatFocasError("FOCAS connect failed", connectResult.rc);
                SetState(ConnectionState.Faulted, error);
                _logger.LogError("FOCAS connect failed: error code {ErrorCode}", connectResult.rc);
                return new() { Success = false, ErrorMessage = error };
            }

            _mapper.SetHandle(_handle);
            _mapper.ConfigureAxisLabels(config.ExtendedProperties.GetValueOrDefault("AxisLabels"));
            SetState(ConnectionState.Connected);
            _logger.LogInformation("Connected to FANUC FOCAS at {Host}:{Port} (handle={Handle})",
                config.Host, port, _handle);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            SetState(ConnectionState.Faulted, ex.Message);
            _logger.LogError(ex, "Failed to connect to FANUC FOCAS at {Host}:{Port}", config.Host, config.Port);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            if (_state == ConnectionState.Disconnected)
                return;

            _api.Disconnect(_handle);
            _handle = 0;
            SetState(ConnectionState.Disconnected);
            _logger.LogInformation("Disconnected from FANUC FOCAS");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during FOCAS disconnect");
            SetState(ConnectionState.Faulted, ex.Message);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_state != ConnectionState.Connected)
            return false;

        await _semaphore.WaitAsync(ct);
        try
        {
            // Lightweight health check: read run status
            int rc = _api.ReadRunStatus(_handle, out _);
            return rc == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ───────────────────────── Single Read ─────────────────────────

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            return _mapper.Read(address, dataType);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ───────────────────────── Batch Read ─────────────────────────

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(
        IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            return _mapper.ReadBatch(requests);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ───────────────────────── Write ─────────────────────────

    public async Task<WriteResult> WriteTagAsync(
        string address, DataType dataType, object value, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            var result = _mapper.Write(address, dataType, value);
            if (result.Success)
                _logger.LogDebug("FOCAS write succeeded: {Address} = {Value}", address, value);
            else
                _logger.LogWarning("FOCAS write failed: {Address} — {Error}", address, result.ErrorMessage);
            return result;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<TransferProgressResult> UploadProgramAsync(
        Stream source, NCProgramMetadata metadata, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        var usePathMode = IsCncMemoryPath(metadata.RemotePath);
        var transferId = Guid.NewGuid().ToString("N");
        var started = false;
        var completed = false;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (usePathMode)
                _logger.LogInformation("FOCAS path upload start requested: {RemotePath}, thread={ThreadId}", metadata.RemotePath, Environment.CurrentManagedThreadId);
            var startRc = usePathMode
                ? _api.StartProgramDownloadAtPath(_handle, NormalizeCncMemoryDirectory(metadata.RemotePath))
                : _api.StartProgramDownload(_handle);
            if (usePathMode)
                _logger.LogInformation("FOCAS path upload start returned: {RemotePath}, rc={ReturnCode}, thread={ThreadId}", metadata.RemotePath, startRc, Environment.CurrentManagedThreadId);
            string? reconnectError = null;
            if (startRc == -8 && TryReconnectAfterEwHandle("FOCAS upload start", out reconnectError))
            {
                startRc = usePathMode
                    ? _api.StartProgramDownloadAtPath(_handle, NormalizeCncMemoryDirectory(metadata.RemotePath))
                    : _api.StartProgramDownload(_handle);
                _logger.LogInformation("FOCAS upload start retry returned: {RemotePath}, rc={ReturnCode}",
                    metadata.RemotePath, startRc);
            }
            else if (startRc == -8 && reconnectError is not null)
            {
                return FailTransfer(transferId, stopwatch, 0, reconnectError);
            }
            if (startRc != 0) return FailTransfer(transferId, stopwatch, 0, FormatDetailedFocasError("FOCAS upload start failed", startRc));
            started = true;
            var totalBytes = metadata.FileSize ?? (source.CanSeek ? source.Length - source.Position : 0);
            var transferred = 0L;
            var buffer = new byte[usePathMode ? 1024 : ProgramChunkSize];
            while (true)
            {
                var read = usePathMode
                    ? source.Read(buffer, 0, buffer.Length)
                    : await source.ReadAsync(buffer.AsMemory(0, ProgramChunkSize), ct);
                if (read == 0) break;
                var pending = read;
                var offset = 0;
                while (pending > 0)
                {
                    var acceptedLength = pending;
                    if (usePathMode)
                        _logger.LogInformation("FOCAS path upload chunk requested: {RemotePath}, pending={Pending}, offset={Offset}, thread={ThreadId}", metadata.RemotePath, pending, offset, Environment.CurrentManagedThreadId);
                    var chunkRc = usePathMode
                        ? _api.DownloadProgramChunkAtPath(_handle, buffer[offset..], pending, out acceptedLength)
                        : _api.DownloadProgramChunk(_handle, buffer, pending);
                    if (usePathMode)
                        _logger.LogInformation("FOCAS path upload chunk returned: {RemotePath}, rc={ReturnCode}, accepted={AcceptedLength}, thread={ThreadId}", metadata.RemotePath, chunkRc, acceptedLength, Environment.CurrentManagedThreadId);
                    if (chunkRc != 0 && chunkRc != 10)
                        return FailTransfer(transferId, stopwatch, transferred, FormatDetailedFocasError("FOCAS upload failed", chunkRc));
                    acceptedLength = usePathMode ? acceptedLength : pending;
                    if (usePathMode && chunkRc == 10 && acceptedLength == 0)
                    {
                        _logger.LogInformation("FOCAS path upload waiting for buffer: {RemotePath}, pending={Pending}", metadata.RemotePath, pending);
                        ct.ThrowIfCancellationRequested();
                        Thread.Sleep(10);
                        continue;
                    }
                    transferred += acceptedLength;
                    progress?.Report(new() { BytesTransferred = transferred, TotalBytes = totalBytes });
                    pending -= acceptedLength;
                    offset += acceptedLength;
                    if (!usePathMode || chunkRc == 0) break;
                }
            }

            var endRc = usePathMode ? _api.EndProgramDownloadAtPath(_handle) : _api.EndProgramDownload(_handle);
            if (endRc != 0) return FailTransfer(transferId, stopwatch, transferred, FormatDetailedFocasError("FOCAS upload finalize failed", endRc));
            completed = true;
            var checksum = await ComputeChecksumAsync(source, ct);
            stopwatch.Stop();
            return new() { Success = true, TransferId = transferId, BytesTransferred = transferred, Duration = stopwatch.Elapsed, Checksum = checksum };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FailTransfer(transferId, stopwatch, 0, ex.Message);
        }
        finally
        {
            if (started && !completed)
                _ = usePathMode ? _api.EndProgramDownloadAtPath(_handle) : _api.EndProgramDownload(_handle);
            _semaphore.Release();
        }
    }

    public async Task<TransferProgressResult> DownloadProgramAsync(
        string remotePath, Stream destination, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        var usePathMode = IsCncMemoryPath(remotePath);
        var transferId = Guid.NewGuid().ToString("N");
        var programNumber = ParseProgramNumber(remotePath);
        if (!usePathMode && programNumber is null) { _semaphore.Release(); return FailTransfer(transferId, Stopwatch.StartNew(), 0, $"FOCAS program number not found in '{remotePath}'"); }
        var started = false;
        var completed = false;
        var stopwatch = Stopwatch.StartNew();
        var transferred = 0L;
        try
        {
            var startRc = usePathMode
                ? _api.StartProgramUploadFromPath(_handle, NormalizeCncMemoryFilePath(remotePath))
                : _api.StartProgramUpload(_handle, programNumber!.Value);
            if (usePathMode && startRc != 0 && programNumber is not null)
            {
                var pathStartError = FormatDetailedFocasError("FOCAS path download start failed", startRc);
                _logger.LogWarning(
                    "FOCAS path download start failed for {RemotePath}: {Error}. Falling back to program number O{ProgramNumber:D4}.",
                    remotePath, pathStartError, programNumber.Value);
                if (startRc == -8 && !TryReconnectAfterEwHandle("FOCAS path download start", out var reconnectError))
                    return FailTransfer(transferId, stopwatch, 0, reconnectError ?? pathStartError);

                startRc = _api.StartProgramUpload(_handle, programNumber.Value);
                if (startRc == 0)
                    usePathMode = false;
            }
            if (startRc != 0) return FailTransfer(transferId, stopwatch, 0, FormatDetailedFocasError("FOCAS download start failed", startRc));
            started = true;
            var buffer = new byte[usePathMode ? 1024 : ProgramChunkSize];
            while (true)
            {
                var chunkRc = usePathMode
                    ? _api.UploadProgramChunkFromPath(_handle, buffer, out var actualLength)
                    : _api.UploadProgramChunk(_handle, buffer, out actualLength);
                if (chunkRc != 0 && chunkRc != 10) return FailTransfer(transferId, stopwatch, transferred, FormatDetailedFocasError("FOCAS download failed", chunkRc));
                if (usePathMode && chunkRc == 10 && actualLength == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    continue;
                }
                if (actualLength == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, actualLength), ct);
                transferred += actualLength;
                progress?.Report(new() { BytesTransferred = transferred, TotalBytes = 0 });
                if (buffer[actualLength - 1] == (byte)'%') break;
            }

            var endRc = usePathMode ? _api.EndProgramUploadFromPath(_handle) : _api.EndProgramUpload(_handle);
            if (endRc != 0) return FailTransfer(transferId, stopwatch, transferred, FormatDetailedFocasError("FOCAS download finalize failed", endRc));
            completed = true;
            var checksum = await ComputeChecksumAsync(destination, ct);
            stopwatch.Stop();
            return new() { Success = true, TransferId = transferId, BytesTransferred = transferred, Duration = stopwatch.Elapsed, Checksum = checksum };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FailTransfer(transferId, stopwatch, transferred, ex.Message);
        }
        finally
        {
            if (started && !completed)
                _ = usePathMode ? _api.EndProgramUploadFromPath(_handle) : _api.EndProgramUpload(_handle);
            _semaphore.Release();
        }
    }

    public Task<TransferProgressResult> ResumeUploadAsync(
        string transferId, string remotePath, Stream source, long offset, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
        => Task.FromResult(new TransferProgressResult
        {
            Success = false,
            TransferId = transferId,
            BytesTransferred = offset,
            Duration = TimeSpan.Zero,
            ErrorMessage = "FOCAS driver does not support resume upload"
        });

    // ───────────────────────── Browse ─────────────────────────

    public async Task<IReadOnlyList<ProgramFileEntry>> BrowseFilesAsync(string? path = null, CancellationToken ct = default)
    {
        var normalized = NormalizeFileBrowsePath(path);
        if (string.IsNullOrEmpty(normalized))
        {
            return
            [
                CreateProgramDirectoryEntry("/Programs", "NC Programs", canUpload: false),
                ..GetBrowseRootEntries()
            ];
        }

        if (string.Equals(normalized, "/Programs", StringComparison.OrdinalIgnoreCase))
        {
            var entries = await ReadProgramEntriesAsync(ct);
            return entries.Select(MapProgramEntry).ToArray();
        }

        if (IsCncMemoryPath(normalized))
            return await ReadCncMemoryEntriesAsync(normalized, ct);

        return [];
    }

    public async Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        var cncMemoryNodes = BrowseCncMemoryPaths(parentPath);
        if (cncMemoryNodes is not null)
            return cncMemoryNodes;

        var normalized = (parentPath ?? "").Trim().Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        if (normalized is "/programs" or "programs")
        {
            EnsureConnected();
            await _semaphore.WaitAsync(ct);
            try
            {
                int rc = _api.ReadProgramDirectory(_handle, out var entries);
                if (rc != 0) return [];
                return entries.Select(e => new AddressNode
                {
                    Path = $"/Programs/O{e.Number:D4}",
                    DisplayName = string.IsNullOrEmpty(e.Comment)
                        ? $"O{e.Number:D4} ({FormatSize(e.Size)})"
                        : $"O{e.Number:D4} - {e.Comment} ({FormatSize(e.Size)})",
                    NodeType = AddressNodeType.Variable,
                    DataType = DataType.String,
                    IsReadable = false,
                    IsWritable = false,
                }).ToList();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        var nodes = _mapper.GetAddressSpace(parentPath);
        if (string.IsNullOrWhiteSpace(parentPath) && GetConfiguredCncMemoryBrowseRoots().Count > 0)
            return [.. nodes, ..GetBrowseRootEntries().Select(entry => CreateCncMemoryFolder(entry.Path, entry.Name, writable: false))];

        return nodes;
    }

    public async Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Path,DisplayName,DataType,Readable,Writable");

        await ExportRecursive(sb, null, ct);

        Stream stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        return stream;
    }

    // ───────────────────────── Dispose ─────────────────────────

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during FOCAS driver DisposeAsync");
        }
        _semaphore.Dispose();
    }

    // ═══════════════════════ Private Helpers ═══════════════════════

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected)
            throw new InvalidOperationException("FANUC FOCAS driver is not connected.");
    }

    private void SetState(ConnectionState newState, string? reason = null)
    {
        var old = _state;
        if (old == newState) return;
        _state = newState;
        StateChanged?.Invoke(this, new() { OldState = old, NewState = newState, Reason = reason });
    }

    private static short? ParseProgramNumber(string remotePath)
    {
        var name = Path.GetFileNameWithoutExtension(remotePath);
        var match = Regex.Match(name, @"O?(?<number>\d+)", RegexOptions.IgnoreCase);
        return match.Success && short.TryParse(match.Groups["number"].Value, out var number) ? number : null;
    }

    private static bool IsCncMemoryPath(string? remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
            return false;
        var normalized = remotePath.Trim().Replace('\\', '/');
        return normalized.Contains('/') &&
            !normalized.StartsWith("/Programs", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCncMemoryDirectory(string remotePath)
        => remotePath.Replace('\\', '/').TrimEnd('/');

    private static string NormalizeCncMemoryFilePath(string remotePath)
        => remotePath.Replace('\\', '/');

    private async Task<IReadOnlyList<FocasProgramDirectoryEntry>> ReadProgramEntriesAsync(CancellationToken ct)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            int rc = _api.ReadProgramDirectory(_handle, out var entries);
            return rc == 0 ? entries : [];
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static ProgramFileEntry MapProgramEntry(FocasProgramDirectoryEntry entry) => new()
    {
        Path = $"/Programs/O{entry.Number:D4}",
        Name = $"O{entry.Number:D4}",
        IsDirectory = false,
        SizeBytes = entry.Size,
        ModifiedAt = entry.ModifiedDate,
        CanDownload = true,
        CanUpload = false,
        HasChildren = false,
        Comment = entry.Comment
    };

    private async Task<IReadOnlyList<ProgramFileEntry>> ReadCncMemoryEntriesAsync(string path, CancellationToken ct)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            int rc = _api.ReadCncMemoryDirectory(_handle, path, out var entries);
            if (rc == 0)
                return entries;
        }
        catch (NotSupportedException)
        {
        }
        finally
        {
            _semaphore.Release();
        }

        return BrowseConfiguredCncMemoryEntries(path);
    }

    private static string NormalizeFileBrowsePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        return path.Trim().Replace('\\', '/').TrimEnd('/');
    }

    private IReadOnlyList<ProgramFileEntry> BrowseConfiguredCncMemoryEntries(string path)
    {
        var nodes = BrowseCncMemoryPaths(path);
        if (nodes is null)
            return [];
        return nodes.Select(node => new ProgramFileEntry
        {
            Path = node.Path,
            Name = GetBrowseNodeName(node.Path),
            IsDirectory = true,
            CanDownload = false,
            CanUpload = node.IsWritable,
            HasChildren = true
        }).ToArray();
    }

    private static ProgramFileEntry CreateProgramDirectoryEntry(string path, string name, bool canUpload = true) => new()
    {
        Path = path,
        Name = name,
        IsDirectory = true,
        CanDownload = false,
        CanUpload = canUpload,
        HasChildren = true
    };

    private IReadOnlyList<ProgramFileEntry> GetBrowseRootEntries()
    {
        var roots = GetConfiguredCncMemoryBrowseRoots();
        if (roots.Count == 0)
            return [CreateProgramDirectoryEntry(CncMemoryRoot, "CNC Memory", canUpload: false)];

        return roots.Select(GetTopLevelBrowseChild)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => CreateProgramDirectoryEntry(path!, GetBrowseNodeName(path!), canUpload: false))
            .ToArray();
    }

    private IReadOnlyList<string> GetConfiguredCncMemoryBrowseRoots()
    {
        if (_config?.ExtendedProperties is null ||
            !_config.ExtendedProperties.TryGetValue(CncMemoryBrowseRootsKey, out var raw) ||
            string.IsNullOrWhiteSpace(raw))
            return [];

        return raw.Split(['\r', '\n', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeCncMemoryDirectory)
            .Where(IsCncMemoryPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<AddressNode>? BrowseCncMemoryPaths(string? parentPath)
    {
        var roots = GetConfiguredCncMemoryBrowseRoots();
        var normalized = string.IsNullOrWhiteSpace(parentPath) ? string.Empty : NormalizeCncMemoryDirectory(parentPath);
        if (string.IsNullOrEmpty(normalized) || roots.Count == 0)
            return null;
        if (!roots.Any(root =>
            root.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith($"{root}/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(root, normalized, StringComparison.OrdinalIgnoreCase)))
            return null;

        var childPaths = roots
            .Where(root => root.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .Select(root => GetNextBrowseChild(normalized, root))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return childPaths.Select(path => CreateCncMemoryFolder(path!, GetBrowseNodeName(path!), writable: true)).ToArray();
    }

    private static string? GetNextBrowseChild(string parentPath, string fullPath)
    {
        if (fullPath.Length <= parentPath.Length)
            return null;
        var remainder = fullPath[parentPath.Length..].Trim('/');
        if (string.IsNullOrWhiteSpace(remainder))
            return null;
        var nextSegment = remainder.Split('/')[0];
        return $"{parentPath.TrimEnd('/')}/{nextSegment}";
    }

    private static string? GetTopLevelBrowseChild(string fullPath)
    {
        var normalized = NormalizeCncMemoryDirectory(fullPath).TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return null;
        var nextSegment = normalized.Split('/')[0];
        return fullPath.StartsWith("//", StringComparison.Ordinal) ? $"//{nextSegment}" : $"/{nextSegment}";
    }

    private static AddressNode CreateCncMemoryFolder(string path, string displayName, bool writable) => new()
    {
        Path = path,
        DisplayName = displayName,
        NodeType = AddressNodeType.Folder,
        IsReadable = false,
        IsWritable = writable
    };

    private static string GetBrowseNodeName(string path)
        => path.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;

    private string FormatDetailedFocasError(string operation, int code)
    {
        var message = FormatFocasError(operation, code);
        if (code != 5) return message;
        var detail = _api.ReadDetailError(_handle);
        if (detail is null) return message;
        var detailMessage = MapFocasDetailError(operation, detail.ErrorNumber);
        return string.IsNullOrWhiteSpace(detailMessage)
            ? $"{message}; detail err_no={detail.ErrorNumber}, err_dtno={detail.DataNumber}"
            : $"{message}; detail {detail.ErrorNumber}: {detailMessage}";
    }

    private static string? MapFocasDetailError(string operation, short errorNumber)
    {
        if (operation.Contains("upload start", StringComparison.OrdinalIgnoreCase))
            return errorNumber == 1 ? "program folder name is wrong" : null;

        if (!operation.Contains("upload finalize", StringComparison.OrdinalIgnoreCase))
            return null;

        return errorNumber switch
        {
            1 => "an unavailable NC program character was detected",
            2 => "TV check rejected a block with an odd character count",
            3 => "program memory is full",
            4 => "the same program number has already been registered",
            5 => "the same program number is currently selected on CNC",
            _ => null
        };
    }

    private bool TryReconnectAfterEwHandle(string operation, out string? errorMessage)
    {
        errorMessage = null;
        if (_config is null)
        {
            errorMessage = $"{operation}: -8 (EW_HANDLE); reconnect skipped because connection config is missing";
            return false;
        }

        try
        {
            if (_handle != 0)
                _api.Disconnect(_handle);

            var port = _config.Port > 0 ? _config.Port : DefaultPort;
            var timeout = _config.ConnectTimeout.TotalMilliseconds > 0
                ? (int)_config.ConnectTimeout.TotalMilliseconds
                : DefaultTimeoutMs;
            var rc = _api.Connect(_config.Host, port, timeout, out var handle);
            if (rc != 0)
            {
                errorMessage = $"{operation}: -8 (EW_HANDLE); reconnect failed: {FormatFocasError("FOCAS reconnect failed", rc)}";
                SetState(ConnectionState.Faulted, errorMessage);
                return false;
            }

            _handle = handle;
            _mapper.SetHandle(_handle);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"{operation}: -8 (EW_HANDLE); reconnect failed: {ex.Message}";
            SetState(ConnectionState.Faulted, errorMessage);
            return false;
        }
    }

    private static string FormatFocasError(string operation, int code)
    {
        var name = code switch
        {
            -8 => "EW_HANDLE",
            -1 => "EW_BUSY",
            2 => "EW_LENGTH",
            5 => "EW_DATA",
            7 => "EW_PROT",
            11 => "EW_PATH",
            12 => "EW_MODE",
            15 => "EW_ALARM",
            17 => "EW_PASSWD",
            _ => null
        };
        return name is null ? $"{operation}: {code}" : $"{operation}: {code} ({name})";
    }

    private static async Task<string?> ComputeChecksumAsync(Stream stream, CancellationToken ct)
    {
        if (!stream.CanSeek) return null;
        stream.Seek(0, SeekOrigin.Begin);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static TransferProgressResult FailTransfer(string transferId, Stopwatch stopwatch, long transferred, string errorMessage)
    {
        stopwatch.Stop();
        return new() { Success = false, TransferId = transferId, BytesTransferred = transferred, Duration = stopwatch.Elapsed, ErrorMessage = errorMessage };
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1}MB"
    };

    private void TrackLateConnectCleanup(Task<(int rc, int handle)> connectTask, string host, int port)
    {
        _ = connectTask.ContinueWith(task =>
        {
            if (task.Status != TaskStatus.RanToCompletion)
                return;

            var (rc, handle) = task.Result;
            if (rc != 0 || handle == 0)
                return;

            try
            {
                _api.Disconnect(handle);
                _logger.LogWarning("Released late FANUC handle {Handle} for {Host}:{Port} after managed timeout",
                    handle, host, port);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release late FANUC handle {Handle} for {Host}:{Port}",
                    handle, host, port);
            }
        }, TaskScheduler.Default);
    }

    private async Task ExportRecursive(StringBuilder sb, string? parentPath, CancellationToken ct)
    {
        var nodes = await BrowseAsync(parentPath, ct);
        foreach (var node in nodes)
        {
            if (node.NodeType == AddressNodeType.Variable)
            {
                sb.AppendLine(
                    $"{node.Path},{node.DisplayName},{node.DataType},{node.IsReadable},{node.IsWritable}");
            }
            else
            {
                // Recurse into folders
                await ExportRecursive(sb, node.Path, ct);
            }
        }
    }
}
