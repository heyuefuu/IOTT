namespace IndustrialIoT.Protocols.FileTransfer.Tests;

using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging.Abstractions;

internal static class FtpRegressionTests
{
    public static async Task RunAll(int port)
    {
        await Regression.Run("FTP network upload, browse, download and resume", () => RoundTrip(port));
        await Regression.Run("FTP skipped resume is not reported transferred", () => SkippedResume(port));
    }

    private static DeviceConnectionConfig Config(int port) => new()
    {
        Host = "127.0.0.1", Port = port, Username = "audit", Password = "audit-fixture",
        ConnectTimeout = TimeSpan.FromSeconds(3), ReadTimeout = TimeSpan.FromSeconds(3),
    };

    private static async Task RoundTrip(int port)
    {
        await using var driver = new FtpTransferDriver(NullLogger<FtpTransferDriver>.Instance);
        var connection = await driver.ConnectAsync(Config(port));
        Regression.Require(connection.Success && await driver.PingAsync(), $"FTP connection failed: {connection.ErrorMessage}");
        var directory = "/transfer-regression-" + Guid.NewGuid().ToString("N");
        var metadata = new NCProgramMetadata { FileName = "O1001.nc", RemotePath = directory };
        var payload = Enumerable.Range(0, 1000).Select(value => (byte)value).ToArray();
        using var source = new MemoryStream(payload);
        var upload = await driver.UploadProgramAsync(source, metadata);
        Regression.Require(upload.Success, $"FTP upload failed: {upload.ErrorMessage}");
        var listing = await driver.BrowseAsync(directory);
        var remotePath = directory + "/" + metadata.FileName;
        Regression.Require(listing.Any(node => node.Path == remotePath), "FTP uploaded file missing from listing");
        using var destination = new MemoryStream();
        var download = await driver.DownloadProgramAsync(remotePath, destination);
        Regression.Require(download.Success && destination.ToArray().SequenceEqual(payload), "FTP download bytes differ");
        using var prefix = new MemoryStream(payload[..17]);
        Regression.Require((await driver.UploadProgramAsync(prefix, metadata)).Success, "FTP prefix upload failed");
        using var resumedSource = new MemoryStream(payload);
        var resumed = await driver.ResumeUploadAsync("resume", remotePath, resumedSource, 17);
        Regression.Require(resumed.Success, $"FTP resume failed: {resumed.ErrorMessage}");
        using var resumedDestination = new MemoryStream();
        var resumedDownload = await driver.DownloadProgramAsync(remotePath, resumedDestination);
        Regression.Require(resumedDownload.Success && resumedDestination.ToArray().SequenceEqual(payload), "FTP resume bytes differ");
    }

    private static async Task SkippedResume(int port)
    {
        await using var driver = new FtpTransferDriver(NullLogger<FtpTransferDriver>.Instance);
        Regression.Require((await driver.ConnectAsync(Config(port))).Success, "FTP connection failed");
        var metadata = new NCProgramMetadata { FileName = "already-complete.nc", RemotePath = "/transfer-regression-" + Guid.NewGuid().ToString("N") };
        using var source = new MemoryStream([1, 2, 3, 4]);
        Regression.Require((await driver.UploadProgramAsync(source, metadata)).Success, "FTP setup upload failed");
        var result = await driver.ResumeUploadAsync("skip", metadata.RemotePath + "/" + metadata.FileName, source, source.Length);
        Regression.Require(!result.Success && result.ErrorMessage?.Contains("Skipped") == true,
            "FTP skipped resume was reported as successful transfer");
    }
}
