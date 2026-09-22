using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Host.Services;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.NCLinkApi;
using IndustrialIoT.Protocols.Registration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

internal static class BatchProgramTransferRegressionTests
{
    public static async Task RunAsync()
    {
        await VerifyNcLinkTransfersAsync();
        await VerifyIndependentChannelsAsync();
    }

    private static async Task VerifyNcLinkTransfersAsync()
    {
        await using var peer = new HttpPeer();
        await peer.StartAsync();
        var device = CreateDevice(new DeviceConnectionConfig
        {
            Host = "127.0.0.1", Port = peer.Port,
            ExtendedProperties = new() { ["DeviceId"] = "batch-cnc", ["ApiBaseUrl"] = $"http://127.0.0.1:{peer.Port}" },
        });
        var factory = new DriverFactory(realNcLink: true);
        using var services = new ServiceCollection().AddSingleton<IDeviceRepository>(new DeviceRepository(device))
            .AddSingleton<IProtocolDriverFactory>(factory).BuildServiceProvider();
        var service = CreateService(services);
        var taskIds = new List<string>();
        try
        {
            foreach (var directory in new[] { "programs", "nested/folder/", "/" })
            {
                peer.Files.Clear();
                var files = new[] { CreateFile("first.nc", [1, 2, 255]), CreateFile("second.nc", [3, 4, 0]) };
                var accepted = await service.QueueUploadAsync(device.Id, directory, files);
                taskIds.Add(accepted.TaskId);
                var task = await WaitForTaskAsync(service, accepted.TaskId);
                TestSupport.Require(task.Status == BatchTransferTaskStatus.Completed && task.CompletedFiles == 2, "NCLink batch upload failed");
                var prefix = directory.Trim('/');
                foreach (var file in files)
                {
                    var key = prefix.Length == 0 ? file.FileName : prefix + "/" + file.FileName;
                    using var content = new MemoryStream();
                    await file.CopyToAsync(content);
                    TestSupport.Require(peer.Files.TryGetValue(key, out var actual) && actual.SequenceEqual(content.ToArray()),
                        "NCLink batch directory did not preserve the full key: " + key);
                }
                TestSupport.Require(peer.Files.Count == 2, "Batch upload overwrote another file or duplicated its name");
            }
            await VerifyDuplicateDownloadsAsync(service, peer, device.Id, taskIds);
            TestSupport.Require(factory.Created.All(protocol => protocol == ProtocolType.NCLinkApi), "NCLink batch used an unexpected protocol");
        }
        finally { await CleanupTasksAsync(service, taskIds); }
    }

    private static async Task VerifyDuplicateDownloadsAsync(BatchProgramTransferTaskService service, HttpPeer peer,
        string deviceId, List<string> taskIds)
    {
        string[] paths = ["dirA/O0001", "dirB/O0001", "dirC/O0001 (2)"];
        byte[][] contents = [[10, 11, 255], [20, 21, 0], [30, 31, 128]];
        peer.Files.Clear();
        for (var index = 0; index < paths.Length; index++) peer.Files[paths[index]] = contents[index];
        var accepted = await service.QueueDownloadAsync(deviceId, paths);
        taskIds.Add(accepted.TaskId);
        var task = await WaitForTaskAsync(service, accepted.TaskId);
        TestSupport.Require(task.Status == BatchTransferTaskStatus.Completed && task.CompletedFiles == paths.Length && task.ArtifactReady,
            "Cross-directory batch download failed");
        var artifact = await service.GetArtifactAsync(task.TaskId);
        TestSupport.Require(artifact?.ContentType == "application/zip", "Batch artifact must be a ZIP response");
        using var archive = ZipFile.OpenRead(artifact!.FilePath);
        TestSupport.Require(archive.Entries.Count == paths.Length &&
            archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == paths.Length,
            "ZIP entry names collided");
        for (var index = 0; index < paths.Length; index++)
        {
            using var source = archive.Entries[index].Open();
            using var bytes = new MemoryStream();
            await source.CopyToAsync(bytes);
            TestSupport.Require(bytes.ToArray().SequenceEqual(contents[index]), "A same-name program was overwritten before ZIP creation");
        }
    }

    private static async Task VerifyIndependentChannelsAsync()
    {
        foreach (var protocol in new[] { ProtocolType.FTP, ProtocolType.SMB, ProtocolType.NFS })
        {
            var transfer = new TransferConnectionConfig
            {
                Protocol = protocol, Host = "configured-file-host", Port = 12345, Username = "fixture-user", Password = "fixture-password",
                ConnectTimeout = TimeSpan.FromSeconds(2), ReadTimeout = TimeSpan.FromSeconds(3),
                ExtendedProperties = new() { ["ShareName"] = "programs", ["MountPoint"] = "fixture-mount" },
            };
            var device = CreateDevice(new DeviceConnectionConfig
            {
                Host = "unused-primary-host", Port = 19001, Transfer = transfer,
                ExtendedProperties = new() { ["transferDeviceId"] = "unused-linked-device", ["DeviceId"] = "machine-sn" },
            });
            var factory = new DriverFactory();
            using var services = new ServiceCollection().AddSingleton<IDeviceRepository>(new DeviceRepository(device))
                .AddSingleton<IProtocolDriverFactory>(factory).BuildServiceProvider();
            var service = CreateService(services);
            var accepted = await service.QueueUploadAsync(device.Id, "programs", [CreateFile("first.nc", [7]), CreateFile("second.nc", [8])]);
            try
            {
                var task = await WaitForTaskAsync(service, accepted.TaskId);
                TestSupport.Require(task.Status == BatchTransferTaskStatus.Completed && factory.Created.Single() == protocol,
                    "Explicit file channel did not take priority over the primary or linked device");
                var connected = factory.Connections.Single();
                TestSupport.Require(connected.Host == transfer.Host && connected.Port == transfer.Port &&
                    connected.Username == transfer.Username && connected.Password == transfer.Password &&
                    connected.ConnectTimeout == transfer.ConnectTimeout && connected.ReadTimeout == transfer.ReadTimeout &&
                    connected.ExtendedProperties["MountPoint"] == "fixture-mount" && connected.ExtendedProperties["ShareName"] == "programs",
                    "Explicit file channel configuration was lost");
                TestSupport.Require(factory.Uploads.Count == 2 && factory.Uploads.All(upload => upload.Path == "programs") &&
                    factory.Uploads.Select(upload => upload.FileName).Distinct().Count() == 2,
                    "Independent file channel paths were rewritten as NCLink keys");
            }
            finally { await CleanupTasksAsync(service, [accepted.TaskId]); }
        }
    }

    private static BatchProgramTransferTaskService CreateService(IServiceProvider services) =>
        new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<BatchProgramTransferTaskService>.Instance);

    private static Device CreateDevice(DeviceConnectionConfig config) => new()
    {
        Id = "batch-cnc", Name = "Batch fixture", Type = DeviceType.CNC, Brand = "HNC", Model = "Fixture",
        Protocol = ProtocolType.NCLinkApi, ConnectionConfig = config,
    };

    private static IFormFile CreateFile(string name, byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", name)
        { Headers = new HeaderDictionary(), ContentType = "application/octet-stream" };

    private static async Task<BatchTransferTaskDto> WaitForTaskAsync(BatchProgramTransferTaskService service, string taskId)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            var task = await service.GetTaskAsync(taskId) ?? throw new InvalidOperationException("Task missing");
            if (task.Status is not (BatchTransferTaskStatus.Pending or BatchTransferTaskStatus.Running)) return task;
            await Task.Delay(20);
        }
        throw new TimeoutException("Batch fixture timed out");
    }

    private static async Task CleanupTasksAsync(BatchProgramTransferTaskService service, IEnumerable<string> taskIds)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IndustrialIoT", "batch-transfer"));
        foreach (var taskId in taskIds)
        {
            var task = await service.GetTaskAsync(taskId);
            if (task?.Status is BatchTransferTaskStatus.Pending or BatchTransferTaskStatus.Running) continue;
            var directory = Path.GetFullPath(Path.Combine(root, taskId));
            if (!Guid.TryParseExact(taskId, "N", out _) || !directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected fixture directory");
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class DeviceRepository(Device device) : IDeviceRepository
    {
        public Task<Device?> GetByIdAsync(string deviceId, CancellationToken ct = default) => Task.FromResult(device.Id == deviceId ? device : null);
        public Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Device>>([device]);
        public Task<IReadOnlyList<Device>> GetByTypeAsync(DeviceType type, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Device>>(device.Type == type ? [device] : []);
        public Task AddAsync(Device value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Device value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string deviceId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class DriverFactory(bool realNcLink = false) : IProtocolDriverFactory
    {
        public ConcurrentQueue<ProtocolType> Created { get; } = new();
        public ConcurrentQueue<DeviceConnectionConfig> Connections { get; } = new();
        public ConcurrentQueue<(string FileName, string Path, byte[] Bytes)> Uploads { get; } = new();
        public IProtocolDriver Create(ProtocolType protocol, string brand, string? model = null)
        {
            Created.Enqueue(protocol);
            return realNcLink && protocol == ProtocolType.NCLinkApi
                ? new NCLinkApiDriver(NullLogger<NCLinkApiDriver>.Instance)
                : new RecordingTransferDriver(this, protocol);
        }
    }

    private sealed class RecordingTransferDriver(DriverFactory factory, ProtocolType protocol) : IProtocolDriver, INCProgramTransfer
    {
        public ProtocolType Protocol => protocol;
        public ConnectionState State => ConnectionState.Connected;
        public DriverCapabilities Capabilities => DriverCapabilities.FileTransfer;
        public bool SupportsResume => false;
        public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
        {
            factory.Connections.Enqueue(config);
            return Task.FromResult(new ConnectionResult { Success = true });
        }
        public async Task<TransferProgressResult> UploadProgramAsync(Stream source, NCProgramMetadata metadata, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
        {
            using var contents = new MemoryStream();
            await source.CopyToAsync(contents, ct);
            factory.Uploads.Enqueue((metadata.FileName, metadata.RemotePath, contents.ToArray()));
            return new() { Success = true, TransferId = Guid.NewGuid().ToString("N"), BytesTransferred = contents.Length };
        }
        public Task<TransferProgressResult> DownloadProgramAsync(string remotePath, Stream destination, IProgress<TransferProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TransferProgressResult> ResumeUploadAsync(string transferId, string remotePath, Stream source, long offset, IProgress<TransferProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
