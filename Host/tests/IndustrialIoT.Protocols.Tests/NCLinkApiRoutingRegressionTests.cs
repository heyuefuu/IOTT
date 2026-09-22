using System.Text;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.NCLinkApi;
using Microsoft.Extensions.Logging.Abstractions;

internal static class NCLinkApiRoutingRegressionTests
{
    private static async Task VerifyLegacyAddressesAsync(NCLinkApiDriver driver, HttpPeer peer)
    {
        const string path = "/MACHINE/CONTROLLER/VARIABLE@REG_R";
        const string query = "?index=12&timeout=3456&offset=2&key=programs%2FO0001";
        foreach (var legacyPath in new[] { "/NC_LINK_ROOT" + path, "/nc_link_root" + path,
            "/NC_LINK_ROOT@0" + path, "  NC_LINK_ROOT\\MACHINE\\CONTROLLER\\VARIABLE@REG_R", "//MACHINE//CONTROLLER/VARIABLE@REG_R" })
        {
            var address = legacyPath + query;
            var read = await driver.ReadTagAsync(address, DataType.Int32);
            TestSupport.Require(read.Address == address && read.Quality == TagQuality.Good && Convert.ToInt32(read.Value) == 42,
                "Legacy address did not resolve to the configured device value");
            var item = peer.DataRequests.Last().Body.GetProperty("items")[0];
            TestSupport.Require(item.GetProperty("path").GetString() == path &&
                item.GetProperty("index").GetInt32() == 12 && item.GetProperty("timeout").GetInt32() == 3456 &&
                item.GetProperty("offset").GetInt32() == 2 && item.GetProperty("key").GetString() == "programs/O0001",
                "Legacy normalization changed query values or did not strip the model root");
        }
        var legacy = "/NC_LINK_ROOT" + path + query;
        TestSupport.Require((await driver.WriteTagAsync(legacy, DataType.Int32, 42)).Success, "Legacy address write failed");
        var values = await driver.ReadTagsAsync([
            new() { Address = legacy, DataType = DataType.Int32 },
            new() { Address = path + "?index=12", DataType = DataType.Int32 },
        ]);
        TestSupport.Require(values.Count == 2 && values.All(value => value.Quality == TagQuality.Good && Convert.ToInt32(value.Value) == 42),
            "Legacy batch read failed");
        var items = peer.DataRequests.Last().Body.GetProperty("items");
        TestSupport.Require(items[0].GetProperty("timeout").GetInt32() == 3456 && items[1].GetProperty("timeout").GetInt32() == 2345,
            "Batch reads must prefer each address timeout, then the device default");
        TestSupport.Require(NCLinkApiAddress.Parse("/NC_LINK_ROOT").Path == "/" &&
            NCLinkApiAddress.Parse("/NC_LINK_ROOT_EXTRA/MACHINE/STATUS").Path == "/NC_LINK_ROOT_EXTRA/MACHINE/STATUS",
            "Model root normalization must respect segment boundaries");
    }

    private static void VerifyStandardVariables(IReadOnlyList<AddressNode> nodes)
    {
        (string Suffix, DataType Type, bool Writable)[] expected =
        [
            ("SYS?index=11", DataType.String, false), ("SYS?index=25", DataType.String, false),
            ("MACRO?index=10000", DataType.Double, false),
            ("REG_X?index=0", DataType.Int32, true), ("REG_Y?index=0", DataType.Int32, true),
            ("REG_R?index=0", DataType.Int32, true), ("REG_F?index=0", DataType.Int32, true),
            ("REG_G?index=0", DataType.Int32, true), ("REG_B?index=0", DataType.Int32, true),
            ("CHAN_0?index=6", DataType.Float, false), ("AXIS_0?index=38", DataType.Double, false),
        ];
        foreach (var (suffix, type, writable) in expected)
        {
            var path = "/MACHINE/CONTROLLER/VARIABLE@" + suffix;
            var node = nodes.SingleOrDefault(candidate => candidate.Path == path);
            TestSupport.Require(node is not null && node.DataType == type && node.IsReadable && node.IsWritable == writable,
                "Original gateway variable contract changed: " + path);
        }
    }

    public static async Task RunAsync()
    {
        await using var unusedPeer = new HttpPeer();
        await using var configuredPeer = new HttpPeer();
        await unusedPeer.StartAsync();
        await configuredPeer.StartAsync();
        var config = new DeviceConnectionConfig
        {
            Host = "127.0.0.1", Port = unusedPeer.Port,
            ConnectTimeout = TimeSpan.FromSeconds(3), ReadTimeout = TimeSpan.FromSeconds(5),
            ExtendedProperties = new()
            {
                ["DeviceId"] = "configured-cnc", ["ApiBaseUrl"] = $"http://127.0.0.1:{configuredPeer.Port}",
                ["DefaultRequestTimeoutMs"] = "2345",
            },
        };
        await using var driver = new NCLinkApiDriver(NullLogger<NCLinkApiDriver>.Instance);
        TestSupport.Require((await driver.ConnectAsync(config)).Success, "Configured NCLink endpoint must connect");
        const string address = "/MACHINE/CONTROLLER/VARIABLE@REG_R?index=12";
        TestSupport.Require((await driver.WriteTagAsync(address, DataType.Int32, 42)).Success, "Configured device must accept writes");
        var read = await driver.ReadTagAsync(address, DataType.Int32);
        TestSupport.Require(read.Quality == TagQuality.Good && Convert.ToInt32(read.Value) == 42, "Configured device readback failed");
        await VerifyLegacyAddressesAsync(driver, configuredPeer);
        configuredPeer.RejectWrites = true;
        var rejected = await driver.WriteTagAsync(address, DataType.Int32, 99);
        TestSupport.Require(!rejected.Success && !string.IsNullOrWhiteSpace(rejected.ErrorMessage), "SUCCESS/[[false]] must remain a write failure");
        read = await driver.ReadTagAsync(address, DataType.Int32);
        TestSupport.Require(Convert.ToInt32(read.Value) == 42, "Rejected write must not alter the fixture value");

        var nodes = await driver.BrowseAsync("/MACHINE/CONTROLLER/VARIABLE");
        VerifyStandardVariables(nodes);
        var bytes = Encoding.UTF8.GetBytes("%\nO0001\n(华中程序)\nM30\n%\n").Concat(new byte[] { 0, 255 }).ToArray();
        foreach (var (remotePath, expectedKey) in new[]
        {
            ("O0001", "O0001"), ("programs/O0002", "programs/O0002"),
            ("programs/", "programs/local.nc"), ("/", "local.nc"),
        })
        {
            using var source = new MemoryStream(bytes);
            var result = await driver.UploadProgramAsync(source, new NCProgramMetadata
            {
                FileName = "local.nc", RemotePath = remotePath, FileSize = bytes.Length,
            });
            TestSupport.Require(result.Success, "Upload failed for " + remotePath);
            TestSupport.Require(configuredPeer.Files.TryGetValue(expectedKey, out var stored) && stored.SequenceEqual(bytes),
                "Remote key or upload bytes changed: " + expectedKey);
            using var destination = new MemoryStream();
            result = await driver.DownloadProgramAsync(expectedKey, destination);
            TestSupport.Require(result.Success && destination.ToArray().SequenceEqual(bytes), "Download bytes changed: " + expectedKey);
        }
        TestSupport.Require((await driver.BrowseFilesAsync()).Count == 4, "File listing must come from configured endpoint");
        TestSupport.Require(unusedPeer.DataRequests.IsEmpty && unusedPeer.Files.IsEmpty, "Host/Port fallback must not override ApiBaseUrl");
        TestSupport.Require(configuredPeer.DataRequests.All(request => request.DeviceId == "configured-cnc"), "SN must reach every device request");
        var requestItems = configuredPeer.DataRequests.First().Body.GetProperty("items");
        TestSupport.Require(requestItems[0].GetProperty("timeout").GetInt32() == 2345, "Configured request timeout was lost");
    }
}
