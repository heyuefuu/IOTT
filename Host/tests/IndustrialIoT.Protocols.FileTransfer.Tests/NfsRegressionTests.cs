namespace IndustrialIoT.Protocols.FileTransfer.Tests;

using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging.Abstractions;

internal static class NfsRegressionTests
{
    private static readonly byte[] Payload = [0, 1, 2, 3, 127, 255];

    public static async Task RunAll()
    {
        await Regression.Run("NFS new directory upload, browse, download and resume", RoundTrip);
        foreach (var path in new[] { "../outside.nc", "nested/../../outside.nc" })
            await Regression.Run($"NFS rejects traversal {path}", () => RejectPath(path));
        await Regression.Run("NFS rejects drive absolute path", () => RejectPath("Z:/outside.nc"));
        await Regression.Run("NFS rejects UNC path", () => RejectPath("//server/share/outside.nc"));
        await Regression.Run("NFS rejects escaping configured root", RejectRoot);
        await Regression.Run("NFS rejects absolute configured root", RejectAbsoluteRoot);
        await Regression.Run("NFS rejects path in file name", RejectFileName);
        await Regression.Run("NFS disconnected driver rejects file access", RejectDisconnected);
    }

    private static async Task RoundTrip()
    {
        using var fixture = new Fixture();
        await using var driver = fixture.Driver;
        Regression.Require((await driver.ConnectAsync(fixture.Config("programs"))).Success, "Connect failed");
        using var source = new MemoryStream(Payload);
        var upload = await driver.UploadProgramAsync(source, new NCProgramMetadata
            { FileName = "O1001.nc", RemotePath = "/new-directory" });
        Regression.Require(upload.Success && File.Exists(Path.Combine(fixture.Mount, "programs/new-directory/O1001.nc")),
            "New directory was treated as a file");
        var nodes = await driver.BrowseAsync("/new-directory");
        Regression.Require(nodes.Any(node => node.Path == "/new-directory/O1001.nc"), "Uploaded program missing from browse");
        using var destination = new MemoryStream();
        var download = await driver.DownloadProgramAsync("/new-directory/O1001.nc", destination);
        Regression.Require(download.Success && destination.ToArray().SequenceEqual(Payload), "Round-trip bytes differ");
        using var resumedSource = new MemoryStream(Payload) { Position = 2 };
        var resume = await driver.ResumeUploadAsync("resume", "/new-directory/O1001.nc", resumedSource, 2);
        Regression.Require(resume.Success && File.ReadAllBytes(Path.Combine(fixture.Mount, "programs/new-directory/O1001.nc"))
            .SequenceEqual(Payload), "Resume bytes differ");
    }

    private static async Task RejectPath(string path)
    {
        using var fixture = new Fixture();
        await using var driver = fixture.Driver;
        await driver.ConnectAsync(fixture.Config());
        using var destination = new MemoryStream();
        await Regression.Reject(async () => await driver.DownloadProgramAsync(path, destination));
        await Regression.Reject(async () => await driver.BrowseAsync(path));
        using var source = new MemoryStream(Payload);
        await Regression.Reject(async () => await driver.ResumeUploadAsync("escape", path, source, 0));
        await Regression.Reject(async () => await driver.UploadProgramAsync(source,
            new NCProgramMetadata { FileName = "O1001.nc", RemotePath = path }));
    }

    private static async Task RejectRoot()
    {
        using var fixture = new Fixture();
        await using var driver = fixture.Driver;
        Regression.Require(!(await driver.ConnectAsync(fixture.Config("../outside"))).Success,
            "Escaping RootRelativePath was accepted");
        Regression.Require(!Directory.Exists(Path.Combine(fixture.BaseDirectory, "outside")), "Created directory outside mount");
    }

    private static async Task RejectAbsoluteRoot()
    {
        using var fixture = new Fixture();
        await using var driver = fixture.Driver;
        Regression.Require(!(await driver.ConnectAsync(fixture.Config(fixture.BaseDirectory))).Success,
            "Absolute RootRelativePath was accepted");
    }

    private static async Task RejectFileName()
    {
        using var fixture = new Fixture();
        await using var driver = fixture.Driver;
        await driver.ConnectAsync(fixture.Config());
        using var source = new MemoryStream(Payload);
        await Regression.Reject(async () => await driver.UploadProgramAsync(source,
            new NCProgramMetadata { FileName = "../outside.nc", RemotePath = "/" }));
    }

    private static async Task RejectDisconnected()
    {
        using var fixture = new Fixture();
        await using var driver = fixture.Driver;
        await driver.ConnectAsync(fixture.Config());
        await driver.DisconnectAsync();
        await Regression.Reject(async () => await driver.BrowseAsync());
        using var source = new MemoryStream(Payload);
        await Regression.Reject(async () => await driver.UploadProgramAsync(source,
            new NCProgramMetadata { FileName = "O1001.nc", RemotePath = "/" }));
    }

    private sealed class Fixture : IDisposable
    {
        public string BaseDirectory { get; } = Path.Combine(Path.GetTempPath(), "iot-file-transfer-" + Guid.NewGuid().ToString("N"));
        public string Mount => Path.Combine(BaseDirectory, "mount");
        public NfsTransferDriver Driver { get; } = new(NullLogger<NfsTransferDriver>.Instance);

        public Fixture() => Directory.CreateDirectory(Mount);
        public DeviceConnectionConfig Config(string root = "") => new()
        {
            Host = "fixture", Port = 0,
            ExtendedProperties = new() { ["MountPoint"] = Mount, ["RootRelativePath"] = root },
        };
        public void Dispose() => Directory.Delete(BaseDirectory, recursive: true);
    }
}
