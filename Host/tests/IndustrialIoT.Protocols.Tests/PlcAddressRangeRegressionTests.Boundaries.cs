using System.Text.Json;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Addressing;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;

internal static partial class PlcAddressRangeRegressionTests
{
    private static readonly PlcAddressArea TestArea = new("DM", "数据区", 0, 32767, DataType.UInt16, index => "DM" + index);

    private static async Task PaginationAndExportAsync()
    {
        var browser = new PlcAddressRangeBrowser([TestArea], "ModelRange", "未验证现场权限");
        var addresses = new HashSet<string>();
        var pending = new Queue<string?>();
        pending.Enqueue(null);
        while (pending.TryDequeue(out var path))
        {
            var nodes = await browser.BrowseAsync(path);
            TestSupport.Require(nodes.Count is > 0 and <= PlcAddressRangeBrowser.PageSize, "Unbounded or empty range page");
            foreach (var node in nodes)
                if (node.NodeType == AddressNodeType.Folder) pending.Enqueue(node.Path);
                else TestSupport.Require(addresses.Add(node.Path), "Pagination duplicated an address");
        }
        TestSupport.Require(addresses.Count == 32768 && addresses.Contains("DM32767"), "Pagination omitted range endpoints");
        foreach (var invalid in new[] { "DM/-1-4", "DM/32768-32769", "DM/4-3", "DM/0-3/x", "DM/0-999999999999", "DM/not-a-page" })
            await ExpectAsync<ArgumentException>(async () => { await browser.BrowseAsync(invalid); });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(async () => { await browser.BrowseAsync(ct: cancelled.Token); });
        await ExpectAsync<OperationCanceledException>(async () => { await browser.ExportAddressSpaceAsync(ExportFormat.JSON, cancelled.Token); });

        var exportBrowser = new PlcAddressRangeBrowser([TestArea with { End = 256 }], "ConfiguredRange", "范围,含\"引号\"");
        await using var json = await exportBrowser.ExportAddressSpaceAsync(ExportFormat.JSON);
        using var document = await JsonDocument.ParseAsync(json);
        var rows = document.RootElement;
        TestSupport.Require(rows.GetArrayLength() == 257 && rows[256].GetProperty("path").GetString() == "DM256", "JSON exported only a visible page");
        TestSupport.Require(rows[0].GetProperty("source").GetString() == "ConfiguredRange" && rows[0].GetProperty("dataType").GetString() == "UInt16",
            "JSON lost metadata or serialized enum numbers");
        await using var csv = await exportBrowser.ExportAddressSpaceAsync(ExportFormat.CSV);
        using var reader = new StreamReader(csv);
        var text = await reader.ReadToEndAsync();
        TestSupport.Require(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 258
            && text.Contains("\"DM256\"") && text.Contains("\"范围,含\"\"引号\"\"\""), "CSV pagination or escaping is wrong");
        await ExpectAsync<NotSupportedException>(async () => { await browser.ExportAddressSpaceAsync(ExportFormat.Excel); });
    }

    private static void ConfigurationValidation()
    {
        foreach (var invalid in new[] { "invalid", "[]", "null", "[null]", "[{\"area\":\"DM\"}]",
            "[{\"area\":\"DM\",\"start\":0.5,\"end\":3}]", "[{\"area\":\"DM\",\"start\":-1,\"end\":3}]",
            "[{\"area\":\"DM\",\"start\":0,\"end\":32768}]", "[{\"area\":\"bad\",\"start\":0,\"end\":3}]",
            "[{\"area\":\"DM\",\"start\":0,\"end\":1},{\"area\":\"dm\",\"start\":2,\"end\":3}]" })
            Expect<AddressSpaceConfigurationException>(() => PlcAddressRangeBrowser.Configure([TestArea], Config(invalid)));
        Expect<AddressSpaceConfigurationException>(() => PlcAddressRangeBrowser.Configure([TestArea], Config(), true));
        var selected = PlcAddressRangeBrowser.Configure([TestArea], Config("[{\"area\":\"dm\",\"start\":4,\"end\":5}]"), true);
        TestSupport.Require(selected[0].Start == 4 && selected[0].End == 5 && TestArea.Start == 0, "Range override mutated shared model metadata");
        Expect<AddressSpaceConfigurationException>(() => new PlcAddressRangeBrowser([TestArea with { End = 1_000_000 }], "ModelRange", ""));
    }

    private static async Task ProfileBoundariesAsync(IProtocolDriverFactory factory)
    {
        await using var omron = factory.Create(ProtocolType.FINS, "Omron");
        var ranges = (IAddressRangeBrowser)omron;
        var cp1e = ranges.CreateAddressSpaceBrowser(Config(), "CP1E-N");
        TestSupport.Require((await cp1e.BrowseAsync())[0].Source == "ModelRange", "Known CPU lost its model profile");
        TestSupport.Require((await cp1e.BrowseAsync("DM/8191-8191"))[0].Path == "DM8191", "CP1E-N boundary wrong");
        await ExpectAsync<ArgumentException>(async () => { await cp1e.BrowseAsync("DM/8192-8192"); });
        var cp1h = ranges.CreateAddressSpaceBrowser(Config(), "CP1H");
        TestSupport.Require((await cp1h.BrowseAsync("DM/32767-32767"))[0].Path == "DM32767", "CP1H used CP1E capacity");
        TestSupport.Require(!(await cp1h.BrowseAsync("AR/0-0"))[0].IsWritable, "Read-only auxiliary area became writable");
        Expect<AddressSpaceConfigurationException>(() => ranges.CreateAddressSpaceBrowser(Config(), "CP1W"));
        var explicitUnknown = ranges.CreateAddressSpaceBrowser(Config("[{\"area\":\"DM\",\"start\":0,\"end\":1}]"), "CP1W");
        TestSupport.Require((await explicitUnknown.BrowseAsync("DM")).Count == 2, "Unknown CPU cannot use an explicit range");

        await using var panasonic = factory.Create(ProtocolType.Mewtocol, "Panasonic");
        var panasonicRanges = (IAddressRangeBrowser)panasonic;
        var bits = panasonicRanges.CreateAddressSpaceBrowser(Config("[{\"area\":\"R\",\"start\":15,\"end\":16}]"), "FP0R-C32");
        TestSupport.Require((await bits.BrowseAsync("R")).Select(node => node.Path).SequenceEqual(new[] { "R0.F", "R1.0" }), "Mewtocol word/bit rollover is wrong");
        Expect<AddressSpaceConfigurationException>(() => panasonicRanges.CreateAddressSpaceBrowser(Config("[{\"area\":\"DT\",\"start\":12315,\"end\":12315}]"), "FP0R-C16"));
        Expect<AddressSpaceConfigurationException>(() => panasonicRanges.CreateAddressSpaceBrowser(Config(), "FP"));

        await using var inovance = factory.Create(ProtocolType.Inovance, "Inovance");
        var inovanceRanges = (IAddressRangeBrowser)inovance;
        Expect<AddressSpaceConfigurationException>(() => inovanceRanges.CreateAddressSpaceBrowser(Config(), "H3U"));
        var h3u = Config("[{\"area\":\"X\",\"start\":7,\"end\":8}]");
        h3u.ExtendedProperties["Series"] = "H3U";
        var octal = inovanceRanges.CreateAddressSpaceBrowser(h3u);
        TestSupport.Require((await octal.BrowseAsync("X")).Select(node => node.Path).SequenceEqual(new[] { "X7", "X10" }), "Inovance octal rollover is wrong");
        var am = Config("[{\"area\":\"MX\",\"start\":7,\"end\":8}]");
        am.ExtendedProperties["Series"] = "AM600";
        var bytes = inovanceRanges.CreateAddressSpaceBrowser(am);
        TestSupport.Require((await bytes.BrowseAsync("MX")).Select(node => node.Path).SequenceEqual(new[] { "MX0.7", "MX1.0" }), "AM byte/bit rollover is wrong");
    }

    private static DeviceConnectionConfig Config(string? json = null) => new()
    {
        Host = "127.0.0.1", Port = 1,
        ExtendedProperties = json is null ? [] : new() { ["addressspaceranges"] = json },
    };

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
