using System.Reflection;
using System.Security.Cryptography;
using HslCommunication.Robot.FANUC;
using IndustrialIoT.Application.DTOs;
using IndustrialIoT.Application.Validators;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.FANUC;
using IndustrialIoT.Protocols.Gsk;
using Microsoft.Extensions.Logging.Abstractions;

internal static class CncRegressionTests
{
    public static async Task GskUploadAsync()
    {
        var api = DispatchProxy.Create<IGskrmApi, GskFileProbe>();
        var probe = (GskFileProbe)(object)api;
        await using var driver = new GskrmTransferDriver(NullLogger<GskrmTransferDriver>.Instance, api);
        var connection = await driver.ConnectAsync(new DeviceConnectionConfig { Host = "fixture", Port = 0 });
        TestSupport.Require(connection.Success, connection.ErrorMessage ?? "GSK connection failed");
        TestSupport.Require(probe.LastPort == 0, "SDK-managed port must not be replaced by an assumed TCP port");
        byte[] bytes = Enumerable.Range(0, 10003).Select(index => (byte)index).ToArray();
        using var source = new MemoryStream(bytes);
        var result = await driver.UploadProgramAsync(source, new() { FileName = "fixture.nc", RemotePath = "/", FileSize = bytes.Length });
        TestSupport.Require(result.Success, result.ErrorMessage ?? "SDK upload failed");
        TestSupport.Require(probe.Content.SequenceEqual(bytes), "SDK reopened content differs");
        TestSupport.Require(result.Checksum == Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "Upload checksum differs");
        TestSupport.Require(probe.SourcePath is not null && !File.Exists(probe.SourcePath), "Upload temporary file leaked");
        TestSupport.Require((await driver.BrowseFilesAsync()).Count == 0, "Empty directory should remain valid");
        probe.DirectoryError = true;
        try { await driver.BrowseFilesAsync(); }
        catch (IOException) { return; }
        throw new InvalidOperationException("SDK directory error was converted into an empty listing");
    }

    public static async Task GskSdkConfigurationAsync()
    {
        var validator = new CreateDeviceRequestValidator();
        var request = new CreateDeviceRequest
        {
            Name = "GSK fixture", Type = DeviceType.CNC, Brand = "GSK", Model = "fixture",
            Host = "127.0.0.1", Protocol = ProtocolType.Gskrm, Port = 0,
            Transfer = new() { Host = "127.0.0.1", Protocol = ProtocolType.GskrmFileTransfer, Port = 0 },
        };
        TestSupport.Require(validator.Validate(request).IsValid, "SDK-managed ports must be accepted for both channels");
        TestSupport.Require(!validator.Validate(request with { Protocol = ProtocolType.NCLinkApi }).IsValid,
            "A zero port must still be rejected for HTTP protocols");
        TestSupport.Require(!validator.Validate(request with { Transfer = request.Transfer with { Protocol = ProtocolType.FTP } }).IsValid,
            "A zero transfer port must still be rejected for FTP");
        foreach (var invalidPort in new[] { -1, 65536 })
            TestSupport.Require(!validator.Validate(request with { Port = invalidPort }).IsValid, "SDK port range must remain bounded");
        var api = DispatchProxy.Create<IGskrmApi, GskFileProbe>();
        await using var driver = new GskrmDriver(NullLogger<GskrmDriver>.Instance, api);
        var connection = await driver.ConnectAsync(new DeviceConnectionConfig { Host = "fixture", Port = 0 });
        TestSupport.Require(connection.Success && ((GskFileProbe)(object)api).LastPort == 0,
            "Acquisition must forward SDK-managed transport without inventing a TCP port");
    }

    public static async Task FanucMetadataAsync()
    {
        var sdkWritesUi = typeof(FanucInterfaceNet).GetMethods().Any(method => method.Name.StartsWith("WriteUI"));
        TestSupport.Require(!sdkWritesUi, "SDK offers UI writes; review advertised capabilities");
        await using var driver = new FanucRobotDriver(NullLogger<FanucRobotDriver>.Instance);
        var crx = await driver.BrowseAsync("CRX");
        var rawUi = await driver.BrowseAsync("IO/UI");
        TestSupport.Require(crx.Count > 0 && crx.All(node => !node.IsWritable), "CRX advertises unsupported UI writes");
        TestSupport.Require(rawUi.Count > 0 && rawUi.All(node => !node.IsWritable), "Raw UI unexpectedly writable");
        var sdi = await driver.BrowseAsync("IO/SDI");
        TestSupport.Require(sdi.All(node => node.IsWritable), "Supported SDI writes changed");
    }
}

public class GskFileProbe : DispatchProxy
{
    public byte[] Content { get; private set; } = [];
    public string? SourcePath { get; private set; }
    public bool DirectoryError { get; set; }
    public int? LastPort { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(arguments);
        if (method.Name == nameof(IGskrmApi.CreateInstance))
        {
            LastPort = (int)arguments[1]!;
            arguments[3] = 1;
            return 0;
        }
        if (method.Name == nameof(IGskrmApi.SendCNCFile))
        {
            SourcePath = (string)arguments[1]!;
            Content = File.ReadAllBytes(SourcePath);
            return 0;
        }
        if (method.Name == nameof(IGskrmApi.GetCNCFileList))
        {
            arguments[1] = Array.Empty<GskrmCncFileEntry>();
            return DirectoryError ? -1 : 0;
        }
        if (method.Name is nameof(IGskrmApi.SetOvertime) or nameof(IGskrmApi.CloseInstance)) return 0;
        throw new NotSupportedException(method.Name);
    }
}
