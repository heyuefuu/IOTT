namespace IndustrialIoT.Host.Services;

using System.Collections.Concurrent;
using System.IO.Compression;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

public sealed class BatchProgramTransferTaskService(IServiceScopeFactory scopeFactory, ILogger<BatchProgramTransferTaskService> logger) : IBatchProgramTransferTaskService
{
    private readonly ConcurrentDictionary<string, BatchTransferTaskState> _tasks = new();
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<BatchProgramTransferTaskService> _logger = logger;

    public async Task<BatchTransferAcceptedResponse> QueueDownloadAsync(string deviceId, IReadOnlyList<string> remotePaths, CancellationToken ct = default)
    {
        var task = BatchTransferTaskState.Create(deviceId, TransferDirection.Download, remotePaths.Select(path => BatchTransferItemState.ForDownload(path)).ToArray());
        _tasks[task.TaskId] = task;
        await Task.Yield();
        _ = Task.Run(() => ExecuteDownloadAsync(task));
        return new() { TaskId = task.TaskId, Status = task.Status };
    }

    public async Task<BatchTransferAcceptedResponse> QueueUploadAsync(string deviceId, string remotePath, IReadOnlyList<IFormFile> files, CancellationToken ct = default)
    {
        var task = BatchTransferTaskState.Create(deviceId, TransferDirection.Upload, Array.Empty<BatchTransferItemState>());
        _tasks[task.TaskId] = task;
        await StageUploadFilesAsync(task, remotePath, files, ct);
        _ = Task.Run(() => ExecuteUploadAsync(task, remotePath));
        return new() { TaskId = task.TaskId, Status = task.Status };
    }

    public Task<BatchTransferTaskDto?> GetTaskAsync(string taskId, CancellationToken ct = default) =>
        Task.FromResult(_tasks.TryGetValue(taskId, out var task) ? task.ToDto() : null);

    public Task<BatchTransferArtifactResult?> GetArtifactAsync(string taskId, CancellationToken ct = default)
    {
        if (!_tasks.TryGetValue(taskId, out var task) || string.IsNullOrWhiteSpace(task.ArtifactPath) || !File.Exists(task.ArtifactPath))
            return Task.FromResult<BatchTransferArtifactResult?>(null);
        return Task.FromResult<BatchTransferArtifactResult?>(new(task.ArtifactPath, task.ArtifactFileName!, "application/zip"));
    }

    private async Task StageUploadFilesAsync(BatchTransferTaskState task, string remotePath, IReadOnlyList<IFormFile> files, CancellationToken ct)
    {
        Directory.CreateDirectory(task.WorkspacePath);
        var items = new List<BatchTransferItemState>(files.Count);
        foreach (var file in files)
        {
            var localPath = Path.Combine(task.WorkspacePath, $"{Guid.NewGuid():N}_{file.FileName}");
            await using var stream = new FileStream(localPath, FileMode.Create, FileAccess.Write);
            await file.CopyToAsync(stream, ct);
            items.Add(BatchTransferItemState.ForUpload(file.FileName, remotePath, localPath, file.Length));
        }
        task.ReplaceItems(items);
    }

    private async Task ExecuteDownloadAsync(BatchTransferTaskState task)
    {
        try
        {
            Directory.CreateDirectory(task.WorkspacePath);
            task.MarkRunning();
            foreach (var item in task.Items)
            {
                await using var context = await CreateTransferContextAsync(task.DeviceId, CancellationToken.None);
                await DownloadOneAsync(task, context, item, CancellationToken.None);
            }
            FinalizeDownloadArtifact(task);
            task.MarkFinished();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch download task failed: {TaskId}", task.TaskId);
            task.MarkFailed(ex.Message);
        }
    }

    private async Task ExecuteUploadAsync(BatchTransferTaskState task, string remotePath)
    {
        try
        {
            await using var context = await CreateTransferContextAsync(task.DeviceId, CancellationToken.None);
            task.MarkRunning();
            foreach (var item in task.Items)
                await UploadOneAsync(task, context, item, remotePath, CancellationToken.None);
            task.MarkFinished();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch upload task failed: {TaskId}", task.TaskId);
            task.MarkFailed(ex.Message);
        }
    }

    private async Task DownloadOneAsync(BatchTransferTaskState task, TransferExecutionContext context, BatchTransferItemState item, CancellationToken ct)
    {
        var localPath = Path.Combine(task.WorkspacePath, item.FileName);
        item.MarkRunning();
        await using var destination = new FileStream(localPath, FileMode.Create, FileAccess.ReadWrite);
        var result = await context.TransferDriver.DownloadProgramAsync(item.RemotePath, destination, ct: ct);
        item.MarkFinished(result.Success, result.BytesTransferred, result.Duration, result.ErrorMessage, localPath);
        task.Accumulate(item);
    }

    private async Task UploadOneAsync(BatchTransferTaskState task, TransferExecutionContext context, BatchTransferItemState item, string remotePath, CancellationToken ct)
    {
        item.MarkRunning();
        await using var source = new FileStream(item.LocalPath!, FileMode.Open, FileAccess.Read);
        var metadata = new NCProgramMetadata { FileName = item.FileName, RemotePath = remotePath, FileSize = source.Length };
        var result = await context.TransferDriver.UploadProgramAsync(source, metadata, ct: ct);
        var finalRemotePath = ResolveRemoteFilePath(context.Protocol, remotePath, item.FileName);
        item.MarkFinished(result.Success, result.BytesTransferred, result.Duration, result.ErrorMessage, finalRemotePath: finalRemotePath);
        task.Accumulate(item);
    }

    private void FinalizeDownloadArtifact(BatchTransferTaskState task)
    {
        var completedFiles = task.Items.Where(item => item.Status == BatchTransferItemStatus.Completed && !string.IsNullOrWhiteSpace(item.LocalPath)).ToArray();
        if (completedFiles.Length == 0) return;
        var artifactPath = Path.Combine(task.WorkspacePath, $"{task.TaskId}.zip");
        using var archive = ZipFile.Open(artifactPath, ZipArchiveMode.Create);
        foreach (var item in completedFiles)
            archive.CreateEntryFromFile(item.LocalPath!, item.FileName);
        task.SetArtifact(artifactPath, $"batch-{task.TaskId}.zip");
    }

    private static string ResolveRemoteFilePath(ProtocolType protocol, string remotePath, string fileName)
    {
        if (protocol == ProtocolType.FOCAS && remotePath.StartsWith("//CNC_MEM/", StringComparison.OrdinalIgnoreCase))
            return $"{remotePath.TrimEnd('/')}/{fileName}";
        var separator = protocol == ProtocolType.SMB ? '\\' : '/';
        var normalized = remotePath.Replace('\\', separator).Replace('/', separator).TrimEnd(separator);
        return string.IsNullOrWhiteSpace(normalized) ? fileName : $"{normalized}{separator}{fileName}";
    }

    private async Task<TransferExecutionContext> CreateTransferContextAsync(string deviceId, CancellationToken ct)
    {
        var scope = _scopeFactory.CreateAsyncScope();
        var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var factory = scope.ServiceProvider.GetRequiredService<IProtocolDriverFactory>();
        var device = await deviceRepo.GetByIdAsync(deviceId, ct) ?? throw new InvalidOperationException($"Device {deviceId} not found");
        var target = await ResolveTransferTargetDeviceAsync(deviceRepo, device, ct);
        var protocol = target.ConnectionConfig.Transfer?.Protocol ?? target.Protocol;
        var config = target.ConnectionConfig.Transfer is null ? target.ConnectionConfig : CreateTransferConfig(target.ConnectionConfig.Transfer);
        var driver = factory.Create(protocol, target.Brand, target.Model);
        var connect = await driver.ConnectAsync(config, ct);
        if (!connect.Success)
        {
            await driver.DisposeAsync();
            await scope.DisposeAsync();
            throw new InvalidOperationException($"Connection failed: {connect.ErrorMessage}");
        }
        if (driver is not INCProgramTransfer transferDriver)
        {
            await driver.DisposeAsync();
            await scope.DisposeAsync();
            throw new InvalidOperationException("Device driver does not support NC program transfer");
        }
        return new(scope, protocol, transferDriver);
    }

    private static async Task<Device> ResolveTransferTargetDeviceAsync(IDeviceRepository deviceRepo, Device device, CancellationToken ct)
    {
        if (device.ConnectionConfig.Transfer is not null) return device;
        if (!device.ConnectionConfig.ExtendedProperties.TryGetValue("transferDeviceId", out var transferDeviceId) || string.IsNullOrWhiteSpace(transferDeviceId) || string.Equals(transferDeviceId, device.Id, StringComparison.OrdinalIgnoreCase))
            return device;
        return await deviceRepo.GetByIdAsync(transferDeviceId, ct) ?? throw new InvalidOperationException($"Transfer device {transferDeviceId} not found for device {device.Id}");
    }

    private static DeviceConnectionConfig CreateTransferConfig(TransferConnectionConfig transfer) => new()
    {
        Host = transfer.Host,
        Port = transfer.Port,
        Username = transfer.Username,
        Password = transfer.Password,
        ConnectTimeout = transfer.ConnectTimeout,
        ReadTimeout = transfer.ReadTimeout,
        ExtendedProperties = new Dictionary<string, string>(transfer.ExtendedProperties)
    };
}

internal sealed class TransferExecutionContext(AsyncServiceScope scope, ProtocolType protocol, INCProgramTransfer transferDriver) : IAsyncDisposable
{
    public ProtocolType Protocol { get; } = protocol;
    public INCProgramTransfer TransferDriver { get; } = transferDriver;

    public async ValueTask DisposeAsync()
    {
        if (TransferDriver is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        await scope.DisposeAsync();
    }
}

internal sealed class BatchTransferItemState
{
    public required string FileName { get; init; }
    public required string RemotePath { get; set; }
    public string? LocalPath { get; private set; }
    public long FileSize { get; private set; }
    public long BytesTransferred { get; private set; }
    public BatchTransferItemStatus Status { get; private set; } = BatchTransferItemStatus.Pending;
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? ErrorMessage { get; private set; }
    public static BatchTransferItemState ForDownload(string remotePath) => new() { FileName = Path.GetFileName(remotePath), RemotePath = remotePath, FileSize = 0 };
    public static BatchTransferItemState ForUpload(string fileName, string remotePath, string localPath, long fileSize) => new() { FileName = fileName, RemotePath = remotePath, LocalPath = localPath, FileSize = fileSize };
    public void MarkRunning() { Status = BatchTransferItemStatus.Running; StartedAt = DateTimeOffset.UtcNow; }
    public void MarkFinished(bool success, long bytesTransferred, TimeSpan duration, string? errorMessage, string? localPath = null, string? finalRemotePath = null) { Status = success ? BatchTransferItemStatus.Completed : BatchTransferItemStatus.Failed; BytesTransferred = bytesTransferred; if (FileSize == 0) FileSize = bytesTransferred; CompletedAt = StartedAt + duration; ErrorMessage = errorMessage; if (!string.IsNullOrWhiteSpace(localPath)) LocalPath = localPath; if (!string.IsNullOrWhiteSpace(finalRemotePath)) RemotePath = finalRemotePath; }
    public BatchTransferItemDto ToDto() => new() { FileName = FileName, RemotePath = RemotePath, Status = Status, FileSize = FileSize, BytesTransferred = BytesTransferred, DurationMs = CompletedAt.HasValue ? (CompletedAt.Value - StartedAt).TotalMilliseconds : null, ErrorMessage = ErrorMessage };
}

internal sealed class BatchTransferTaskState
{
    private readonly object _gate = new();
    private readonly List<BatchTransferItemState> _items;
    private BatchTransferTaskState(string deviceId, TransferDirection direction, IReadOnlyList<BatchTransferItemState> items) { TaskId = Guid.NewGuid().ToString("N"); DeviceId = deviceId; Direction = direction; StartedAt = DateTimeOffset.UtcNow; WorkspacePath = Path.Combine(Path.GetTempPath(), "IndustrialIoT", "batch-transfer", TaskId); _items = [.. items]; }
    public string TaskId { get; }
    public string DeviceId { get; }
    public TransferDirection Direction { get; }
    public string WorkspacePath { get; }
    public BatchTransferTaskStatus Status { get; private set; } = BatchTransferTaskStatus.Pending;
    public string? ArtifactPath { get; private set; }
    public string? ArtifactFileName { get; private set; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public IReadOnlyList<BatchTransferItemState> Items { get { lock (_gate) return _items.ToArray(); } }
    public static BatchTransferTaskState Create(string deviceId, TransferDirection direction, IReadOnlyList<BatchTransferItemState> items) => new(deviceId, direction, items);
    public void ReplaceItems(IEnumerable<BatchTransferItemState> items) { lock (_gate) { _items.Clear(); _items.AddRange(items); } }
    public void MarkRunning() => Status = BatchTransferTaskStatus.Running;
    public void Accumulate(BatchTransferItemState item) { }
    public void SetArtifact(string artifactPath, string artifactFileName) { ArtifactPath = artifactPath; ArtifactFileName = artifactFileName; }
    public void MarkFailed(string errorMessage)
    {
        lock (_gate)
        {
            Status = BatchTransferTaskStatus.Failed;
            CompletedAt = DateTimeOffset.UtcNow;
            if (_items.Count == 0)
            {
                var item = BatchTransferItemState.ForDownload(string.Empty);
                item.MarkRunning();
                item.MarkFinished(false, 0, TimeSpan.Zero, errorMessage);
                _items.Add(item);
            }
        }
    }
    public void MarkFinished() { var failed = _items.Count(item => item.Status == BatchTransferItemStatus.Failed); var completed = _items.Count(item => item.Status == BatchTransferItemStatus.Completed); Status = failed == 0 ? BatchTransferTaskStatus.Completed : completed > 0 ? BatchTransferTaskStatus.PartialSuccess : BatchTransferTaskStatus.Failed; CompletedAt = DateTimeOffset.UtcNow; }
    public BatchTransferTaskDto ToDto() { lock (_gate) { var items = _items.Select(item => item.ToDto()).ToArray(); var durationMs = CompletedAt.HasValue ? (double?)(CompletedAt.Value - StartedAt).TotalMilliseconds : null; var bytes = items.Sum(item => item.BytesTransferred); return new() { TaskId = TaskId, DeviceId = DeviceId, Direction = Direction, Status = Status, TotalFiles = items.Length, CompletedFiles = items.Count(item => item.Status == BatchTransferItemStatus.Completed), FailedFiles = items.Count(item => item.Status == BatchTransferItemStatus.Failed), TotalBytes = items.Sum(item => item.FileSize), BytesTransferred = bytes, Items = items, StartedAt = StartedAt, CompletedAt = CompletedAt, DurationMs = durationMs, ThroughputBytesPerSecond = durationMs is > 0 ? bytes / (durationMs.Value / 1000.0) : null, ArtifactReady = !string.IsNullOrWhiteSpace(ArtifactPath), ArtifactFileName = ArtifactFileName }; } }
}
