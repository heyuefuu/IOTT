namespace IndustrialIoT.Protocols.FileTransfer;

using System.Diagnostics;
using System.Security.Cryptography;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;

/// <summary>
/// NFS 文件传输驱动 — 通过预挂载的 NFS export 目录做文件 IO。
/// 部署模型：运维侧先把远端 NFS export 挂载到本地（Windows: "mount -o anon \\host\export Z:"；
/// Linux: "mount -t nfs host:/export /mnt/cnc"）；驱动通过 ExtendedProperties["MountPoint"]
/// 定位已挂载目录，所有 Upload/Download 都在挂载点内部完成。
///
/// 必需 ExtendedProperties:
///   MountPoint — 已挂载的本地路径，e.g. "Z:\" 或 "/mnt/cnc"
/// 可选:
///   RootRelativePath — 传输操作的根目录（相对 MountPoint），默认空
/// </summary>
[ProtocolDriver(ProtocolType.NFS, "NFS", "FANUC", "Makino", "*")]
public sealed class NfsTransferDriver : IProtocolDriver, INCProgramTransfer, IAddressSpaceBrowser
{
    private const int BufferSize = 81920;
    private readonly ILogger<NfsTransferDriver> _logger;
    private string? _mountPoint;
    private string? _rootDir;
    private ConnectionState _state = ConnectionState.Disconnected;

    public NfsTransferDriver(ILogger<NfsTransferDriver> logger) => _logger = logger;

    public ProtocolType Protocol => ProtocolType.NFS;
    public ConnectionState State => _state;
    public DriverCapabilities Capabilities => DriverCapabilities.FileTransfer | DriverCapabilities.Browse;
    public bool SupportsResume => true;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        SetState(ConnectionState.Connecting);
        try
        {
            var mount = config.ExtendedProperties.GetValueOrDefault("MountPoint");
            if (string.IsNullOrWhiteSpace(mount))
                throw new InvalidOperationException("ExtendedProperties['MountPoint'] is required for NFS driver");
            if (!Directory.Exists(mount))
                throw new DirectoryNotFoundException($"NFS mount point '{mount}' does not exist — please mount first");

            _mountPoint = Path.GetFullPath(mount);
            var rel = config.ExtendedProperties.GetValueOrDefault("RootRelativePath") ?? "";
            _rootDir = ResolveWithinRoot(_mountPoint, rel);
            Directory.CreateDirectory(_rootDir);

            _logger.LogInformation("NFS driver attached to mount point {MountPoint} (root={Root})", mount, _rootDir);
            SetState(ConnectionState.Connected);
            return Task.FromResult<ConnectionResult>(new() { Success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NFS driver connect failed");
            SetState(ConnectionState.Faulted, ex.Message);
            return Task.FromResult<ConnectionResult>(new() { Success = false, ErrorMessage = ex.Message });
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default) { SetState(ConnectionState.Disconnected); return Task.CompletedTask; }

    public Task<bool> PingAsync(CancellationToken ct = default)
        => Task.FromResult(_state == ConnectionState.Connected && _rootDir is not null && Directory.Exists(_rootDir));

    // ── Read/Write tag 不支持 ──
    public Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
        => throw new NotSupportedException("NFS driver does not support tag reads");
    public Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
        => throw new NotSupportedException("NFS driver does not support tag reads");
    public Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
        => Task.FromResult<WriteResult>(new() { Success = false, ErrorMessage = "NFS driver is file-only" });

    public async Task<TransferProgressResult> UploadProgramAsync(Stream source, NCProgramMetadata metadata,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        var target = ResolvePath(metadata.RemotePath, metadata.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var sw = Stopwatch.StartNew();
        long bytes = 0;
        var total = metadata.FileSize ?? source.Length;
        using var sha = SHA256.Create();

        await using (var dest = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            var buf = new byte[BufferSize]; int n;
            while ((n = await source.ReadAsync(buf, ct)) > 0)
            {
                await dest.WriteAsync(buf.AsMemory(0, n), ct);
                sha.TransformBlock(buf, 0, n, null, 0);
                bytes += n;
                progress?.Report(new() { BytesTransferred = bytes, TotalBytes = total });
            }
            sha.TransformFinalBlock([], 0, 0);
        }
        sw.Stop();
        return new() { Success = true, TransferId = Guid.NewGuid().ToString("N"),
            BytesTransferred = bytes, Duration = sw.Elapsed,
            Checksum = Convert.ToHexString(sha.Hash!) };
    }

    public async Task<TransferProgressResult> DownloadProgramAsync(string remotePath, Stream destination,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        var src = ResolvePath(remotePath, null);
        if (!File.Exists(src)) return new() { Success = false, TransferId = "", ErrorMessage = $"Remote file not found: {remotePath}" };

        var sw = Stopwatch.StartNew();
        long bytes = 0;
        var total = new FileInfo(src).Length;
        using var sha = SHA256.Create();

        await using (var s = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
        {
            var buf = new byte[BufferSize]; int n;
            while ((n = await s.ReadAsync(buf, ct)) > 0)
            {
                await destination.WriteAsync(buf.AsMemory(0, n), ct);
                sha.TransformBlock(buf, 0, n, null, 0);
                bytes += n;
                progress?.Report(new() { BytesTransferred = bytes, TotalBytes = total });
            }
            sha.TransformFinalBlock([], 0, 0);
        }
        sw.Stop();
        return new() { Success = true, TransferId = Guid.NewGuid().ToString("N"),
            BytesTransferred = bytes, Duration = sw.Elapsed,
            Checksum = Convert.ToHexString(sha.Hash!) };
    }

    public async Task<TransferProgressResult> ResumeUploadAsync(string transferId, string remotePath, Stream source,
        long offset, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        var target = ResolvePath(remotePath, null);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var sw = Stopwatch.StartNew();
        long bytes = offset;

        await using (var dest = new FileStream(target, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            dest.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[BufferSize]; int n;
            while ((n = await source.ReadAsync(buf, ct)) > 0)
            {
                await dest.WriteAsync(buf.AsMemory(0, n), ct);
                bytes += n;
                progress?.Report(new() { BytesTransferred = bytes, TotalBytes = bytes });
            }
        }
        sw.Stop();
        return new() { Success = true, TransferId = transferId, BytesTransferred = bytes, Duration = sw.Elapsed };
    }

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        var baseDir = ResolvePath(parentPath ?? "", null);
        if (!Directory.Exists(baseDir)) return Task.FromResult<IReadOnlyList<AddressNode>>([]);

        var nodes = Directory.EnumerateFileSystemEntries(baseDir).Select(p =>
        {
            var isDir = Directory.Exists(p);
            var rel = Path.GetRelativePath(_rootDir!, p).Replace('\\', '/');
            return new AddressNode
            {
                Path = "/" + rel,
                DisplayName = Path.GetFileName(p),
                NodeType = isDir ? AddressNodeType.Folder : AddressNodeType.Variable,
                IsReadable = true, IsWritable = !isDir,
            };
        }).ToList();
        return Task.FromResult<IReadOnlyList<AddressNode>>(nodes);
    }

    public Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
        => throw new NotSupportedException("NFS driver browses live filesystem — export not meaningful");

    public ValueTask DisposeAsync() { SetState(ConnectionState.Disconnected); GC.SuppressFinalize(this); return ValueTask.CompletedTask; }

    private string ResolvePath(string remotePath, string? fileName)
    {
        if (_state != ConnectionState.Connected || _rootDir is null)
            throw new InvalidOperationException("Not connected");
        if (remotePath.StartsWith("//", StringComparison.Ordinal) || remotePath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("NFS paths must be relative to the configured root");
        var relative = remotePath.TrimStart('/', '\\');
        if (fileName is not null)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." || fileName.IndexOfAny(['/', '\\', ':']) >= 0)
                throw new ArgumentException("NFS file name must contain a single file name", nameof(fileName));
            relative = Path.Combine(relative, fileName);
        }
        return ResolveWithinRoot(_rootDir, relative);
    }

    private static string ResolveWithinRoot(string root, string relativePath)
    {
        var relative = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new UnauthorizedAccessException("NFS paths must be relative to the configured root");
        var fullRoot = Path.GetFullPath(root);
        var resolved = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootPrefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!resolved.Equals(fullRoot, comparison) && !resolved.StartsWith(rootPrefix, comparison))
            throw new UnauthorizedAccessException("NFS path escapes the configured root");
        if (resolved.Equals(fullRoot, comparison)) return resolved;

        var current = fullRoot;
        foreach (var part in Path.GetRelativePath(fullRoot, resolved).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("NFS paths cannot traverse symbolic links or reparse points");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return resolved;
    }

    private void SetState(ConnectionState next, string? reason = null)
    {
        var old = _state;
        if (old == next) return;
        _state = next;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs { OldState = old, NewState = next, Reason = reason });
    }
}
