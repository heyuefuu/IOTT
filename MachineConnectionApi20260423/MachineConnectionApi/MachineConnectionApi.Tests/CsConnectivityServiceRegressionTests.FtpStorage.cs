namespace MachineConnectionApi.Tests;

using System.Text;
using FluentFTP;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;

internal static partial class CsConnectivityServiceRegressionTests
{
    private static async Task FtpLocalFilesSurviveRestart()
    {
        using var sandbox = new FtpStorageSandbox();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        const string seededName = "预置.txt";
        var seeded = Encoding.UTF8.GetBytes("existing root content");
        var payload = new byte[] { 0, 1, 127, 128, 255, 13, 10 };
        Directory.CreateDirectory(Path.Combine(sandbox.Root, "alpha"));
        Directory.CreateDirectory(Path.Combine(sandbox.Root, "beta"));
        File.WriteAllBytes(Path.Combine(sandbox.Root, seededName), seeded);
        File.WriteAllText(Path.Combine(sandbox.Root, "alpha", "same.txt"), "alpha");
        File.WriteAllText(Path.Combine(sandbox.Root, "beta", "same.txt"), "beta");
        string serverId;
        using (var service = Create(sandbox.ConfigurationPath))
        {
            var server = await StartStorageFtp(service, sandbox.Root);
            serverId = server.Id;
            using var client = StorageFtpClient(server);
            await client.Connect(token);
            var listing = await client.GetListing("/", token);
            AssertEqual(true, listing.Any(item => item.Name == seededName && item.Type == FtpObjectType.File), "DiskSeedListing");
            AssertEqual(true, listing.Any(item => item.Name == "alpha" && item.Type == FtpObjectType.Directory), "DiskSubdirectoryListing");
            AssertFtpBytes(seeded, await client.DownloadBytes("/" + seededName, token), "DiskSeedDownload");
            await client.SetWorkingDirectory("/alpha", token);
            AssertEqual("/alpha", await client.GetWorkingDirectory(token), "DiskWorkingDirectory");
            AssertFtpBytes(Encoding.UTF8.GetBytes("alpha"), await client.DownloadBytes("same.txt", token), "RelativeDownload");
            AssertEqual(FtpStatus.Success, await client.UploadBytes(payload, "same.txt", token: token), "RelativeUpload");
            AssertFtpBytes(payload, File.ReadAllBytes(Path.Combine(sandbox.Root, "alpha", "same.txt")), "SubdirectoryUploadOnDisk");
            AssertEqual("beta", File.ReadAllText(Path.Combine(sandbox.Root, "beta", "same.txt")), "SameNameIsolation");
            await client.SetWorkingDirectory("/", token);
            AssertEqual(FtpStatus.Success, await client.UploadBytes(payload, "/uploaded.bin", token: token), "DiskUpload");
            AssertFtpBytes(payload, File.ReadAllBytes(Path.Combine(sandbox.Root, "uploaded.bin")), "DiskUploadContent");
            AssertEqual(true, (await client.GetListing("/alpha", token)).Any(item => item.Name == "same.txt"), "ChildListing");
            await client.Disconnect(token);
            AssertEqual(true, service.StopServer(server.Id), "DiskServerStopped");
        }
        using (var rebuilt = Create(sandbox.ConfigurationPath))
        {
            var server = rebuilt.ListServers().Single();
            AssertEqual(serverId, server.Id, "RestartServerId");
            AssertEqual(sandbox.Root, server.RootDirectory, "RestartRootDirectory");
            AssertEqual("停止", server.Status, "RestartStoppedStatus");
            server.Port = GetFreePort();
            rebuilt.UpsertServer(server);
            await rebuilt.StartServerAsync(server.Id);
            using var client = StorageFtpClient(server);
            await client.Connect(token);
            AssertFtpBytes(payload, await client.DownloadBytes("/uploaded.bin", token), "RestartDiskDownload");
            AssertFtpBytes(payload, await client.DownloadBytes("/alpha/same.txt", token), "RestartSubdirectoryDownload");
            AssertFtpBytes(Encoding.UTF8.GetBytes("beta"), await client.DownloadBytes("/beta/same.txt", token), "RestartSameNameIsolation");
            await client.DeleteFile("/uploaded.bin", token);
            AssertEqual(false, File.Exists(Path.Combine(sandbox.Root, "uploaded.bin")), "DiskDelete");
            await client.Disconnect(token);
        }
    }

    private static async Task FtpEmptyRootKeepsMemoryBehavior()
    {
        foreach (var root in new string?[] { null, "" })
        {
            using var sandbox = new FtpStorageSandbox();
            using var service = Create(sandbox.ConfigurationPath);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var token = timeout.Token;
            var server = await StartStorageFtp(service, root);
            var payload = Encoding.UTF8.GetBytes("memory content");
            using (var client = StorageFtpClient(server))
            {
                await client.Connect(token);
                AssertEqual(FtpStatus.Success, await client.UploadBytes(payload, "/memory.txt", token: token), "MemoryUpload");
                AssertFtpBytes(payload, await client.DownloadBytes("/memory.txt", token), "MemoryDownload");
                await client.DeleteFile("/memory.txt", token);
                AssertEqual(0, (await client.GetListing("/", token)).Length, "MemoryDelete");
                AssertEqual(FtpStatus.Success, await client.UploadBytes(payload, "/volatile.txt", token: token), "VolatileUpload");
                await client.Disconnect(token);
            }
            service.StopServer(server.Id);
            server.Port = GetFreePort();
            service.UpsertServer(server);
            await service.StartServerAsync(server.Id);
            using var afterRestart = StorageFtpClient(server);
            await afterRestart.Connect(token);
            AssertEqual(0, (await afterRestart.GetListing("/", token)).Length, "MemoryClearedAfterStop");
            AssertEqual(0, Directory.GetFiles(sandbox.Root).Length, "MemoryDoesNotWriteDiskFiles");
            await afterRestart.Disconnect(token);
        }
    }

    private static async Task<CsServerService> StartStorageFtp(CsConnectivityService service, string? root)
    {
        var server = service.UpsertServer(new CsServerService
        {
            Name = "storage fixture", Type = "FtpServer", Port = GetFreePort(),
            Username = "ftp-fixture", Password = "fixture-secret", RootDirectory = root,
        });
        AssertEqual(true, await service.StartServerAsync(server.Id), "StorageServerStarted");
        return server;
    }

    private static AsyncFtpClient StorageFtpClient(CsServerService server)
    {
        var client = new AsyncFtpClient("127.0.0.1", server.Username, server.Password, server.Port);
        client.Encoding = Encoding.UTF8;
        client.Config.DataConnectionType = FtpDataConnectionType.PASV;
        client.Config.ConnectTimeout = client.Config.ReadTimeout = 5000;
        client.Config.DataConnectionConnectTimeout = client.Config.DataConnectionReadTimeout = 5000;
        return client;
    }

    private static void AssertFtpBytes(byte[] expected, byte[]? actual, string name)
    {
        if (actual is null || !expected.SequenceEqual(actual))
            throw new InvalidOperationException(name + ": downloaded or stored bytes differ");
    }

    private sealed class FtpStorageSandbox : IDisposable
    {
        public string DirectoryPath { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cs-ftp-storage-" + Guid.NewGuid().ToString("N")));
        public string Root => Path.Combine(DirectoryPath, "root");
        public string ConfigurationPath => Path.Combine(DirectoryPath, "cs.json");
        public FtpStorageSandbox() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            var fullPath = Path.GetFullPath(DirectoryPath);
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullPath).StartsWith("cs-ftp-storage-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove an unexpected test directory");
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
        }
    }
}
