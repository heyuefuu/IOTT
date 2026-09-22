using System.Diagnostics;
using System.Text;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.MTConnect;
using IndustrialIoT.Protocols.NCLinkApi;
using Microsoft.Extensions.Logging.Abstractions;

internal static class HttpRegressionTests
{
    public static async Task RunAsync()
    {
        await using var peer = new HttpPeer();
        await peer.StartAsync();
        var config = new DeviceConnectionConfig
        {
            Host = "127.0.0.1", Port = peer.Port,
            ConnectTimeout = TimeSpan.FromSeconds(5), ReadTimeout = TimeSpan.FromSeconds(5),
            ExtendedProperties = new() { ["DeviceId"] = "fixture" },
        };
        await Run("NCLinkApi", async () =>
        {
            await using var driver = new NCLinkApiDriver(NullLogger<NCLinkApiDriver>.Instance);
            var connection = await driver.ConnectAsync(config);
            TestSupport.Require(connection.Success, "connect failed: " + connection.ErrorMessage);
            TestSupport.Require(await driver.PingAsync(), "status ping failed");
            const string address = "/MACHINE/CONTROLLER/VARIABLE/COMMON";
            TestSupport.Require((await driver.WriteTagAsync(address, DataType.Double, 12.5)).Success, "write failed");
            var read = await driver.ReadTagAsync(address, DataType.Double);
            TestSupport.Require(read.Quality == TagQuality.Good && Convert.ToDouble(read.Value) == 12.5, "readback failed");
            var batch = await driver.ReadTagsAsync([new() { Address = address, DataType = DataType.Double }]);
            TestSupport.Require(batch.Single().Quality == TagQuality.Good, "batch read failed");
            var transfer = (INCProgramTransfer)driver;
            var bytes = Encoding.ASCII.GetBytes("%\nO0001\nG01 X1.5\nM30\n%\n");
            using var source = new MemoryStream(bytes);
            TestSupport.Require((await transfer.UploadProgramAsync(source, new()
            {
                FileName = "O0001.nc", RemotePath = "programs", FileSize = bytes.Length,
            })).Success, "upload failed");
            var files = await ((IProgramFileBrowser)driver).BrowseFilesAsync();
            TestSupport.Require(files.Any(file => file.Path == "programs/O0001.nc"), "file listing failed");
            using var downloaded = new MemoryStream();
            TestSupport.Require((await transfer.DownloadProgramAsync("programs/O0001.nc", downloaded)).Success, "download failed");
            TestSupport.Require(downloaded.ToArray().SequenceEqual(bytes), "file bytes changed");
        });
        await Run("NCLinkApi rejects offline status and connection timeout", async () =>
        {
            foreach (var deviceId in new[] { "offline", "silent" })
            {
                await using var driver = new NCLinkApiDriver(NullLogger<NCLinkApiDriver>.Instance);
                var watch = Stopwatch.StartNew();
                var connected = await driver.ConnectAsync(config with
                {
                    ConnectTimeout = TimeSpan.FromMilliseconds(150),
                    ExtendedProperties = new() { ["DeviceId"] = deviceId },
                });
                TestSupport.Require(!connected.Success, deviceId + " was falsely reported connected");
                TestSupport.Require(watch.Elapsed < TimeSpan.FromSeconds(2), "connect timeout not enforced");
            }
        });
        await Run("MTConnect", async () =>
        {
            await using var driver = new MTConnectDriver(NullLogger<MTConnectDriver>.Instance);
            TestSupport.Require((await driver.ConnectAsync(config)).Success, "probe handshake failed");
            TestSupport.Require(await driver.PingAsync(), "probe ping failed");
            var data = await driver.ReadTagsAsync([
                new() { Address = "count", DataType = DataType.Int32 },
                new() { Address = "feed", DataType = DataType.Float },
                new() { Address = "state", DataType = DataType.String },
            ]);
            TestSupport.Require(data.All(item => item.Quality == TagQuality.Good), "current data failed");
            var roots = await ((IAddressSpaceBrowser)driver).BrowseAsync();
            var paths = Flatten(roots).Select(node => node.Path).ToHashSet();
            TestSupport.Require(paths.IsSupersetOf(["count", "state", "feed"]),
                "nested DataItems absent from probe browse: " + string.Join(",", paths));
            TestSupport.Require(!(await driver.WriteTagAsync("count", DataType.Int32, 1)).Success,
                "MTConnect should reject writes without a vendor adapter");
        });

        async Task Run(string name, Func<Task> action)
        {
            await action();
            Console.WriteLine("  PASS " + name);
        }
    }

    private static IEnumerable<AddressNode> Flatten(IEnumerable<AddressNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node.Children is not null)
                foreach (var child in Flatten(node.Children)) yield return child;
        }
    }
}
