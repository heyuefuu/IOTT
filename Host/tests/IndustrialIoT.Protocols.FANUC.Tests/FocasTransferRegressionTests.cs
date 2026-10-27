namespace IndustrialIoT.Protocols.FANUC.Tests;

using System.Reflection;
using System.Text;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging.Abstractions;

public class FocasTransferProbeApi : DispatchProxy
{
    private readonly IFocasApi inner = new SimulatedFocasApi();
    private int sendCalls;
    private int receiveCalls;
    public HashSet<int> BusySendCalls { get; } = [];
    public HashSet<int> BusyReceiveCalls { get; } = [];
    public bool AlwaysBusy { get; set; }
    public CancellationTokenSource? CancelWhenBusy { get; set; }
    public int FinalizeCalls { get; private set; }
    public int ProgramDirectoryReturnCode { get; set; }
    public IReadOnlyList<FocasProgramDirectoryEntry>? ProgramDirectoryEntries { get; set; }

    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(arguments);
        if (method.Name == nameof(IFocasApi.ReadProgramDirectory) && ProgramDirectoryEntries is not null)
        {
            arguments[1] = ProgramDirectoryEntries;
            return ProgramDirectoryReturnCode;
        }
        if (method.Name.StartsWith("EndProgram", StringComparison.Ordinal)) FinalizeCalls++;
        var sending = method.Name is nameof(IFocasApi.DownloadProgramChunk) or nameof(IFocasApi.DownloadProgramChunkAtPath);
        var receiving = method.Name is nameof(IFocasApi.UploadProgramChunk) or nameof(IFocasApi.UploadProgramChunkFromPath);
        if (sending && (AlwaysBusy || BusySendCalls.Contains(++sendCalls))
            || receiving && (AlwaysBusy || BusyReceiveCalls.Contains(++receiveCalls)))
        {
            CancelWhenBusy?.Cancel();
            if (receiving) arguments[2] = 0;
            if (sending && arguments.Length == 4) arguments[3] = arguments[2];
            return 10;
        }
        if (method.Name == nameof(IFocasApi.DownloadProgramChunkAtPath))
        {
            var accepted = Math.Min((int)arguments[2]!, 128);
            arguments[3] = accepted;
            return inner.DownloadProgramChunk((int)arguments[0]!, (byte[])arguments[1]!, accepted);
        }
        return method.Invoke(inner, arguments);
    }
}

internal static class FocasTransferRegressionTests
{
    public static async Task RunAll()
    {
        foreach (var path in new[] { "/Programs/O9001", "//CNC_MEM/USER/PATH1/O9001" })
            await BufferBusyPreservesContents(path);
        await BufferWaitTerminates(false);
        await BufferWaitTerminates(true);
        await ProgramDirectoryErrorsRemainVisible();
        Console.WriteLine("  FOCAS transfers: busy buffers, partial writes, timeout and cancellation passed");
    }

    private static async Task ProgramDirectoryErrorsRemainVisible()
    {
        var api = DispatchProxy.Create<IFocasApi, FocasTransferProbeApi>();
        var probe = (FocasTransferProbeApi)(object)api;
        await using var driver = new FocasDriver(NullLogger<FocasDriver>.Instance, api);
        var connected = await driver.ConnectAsync(new DeviceConnectionConfig { Host = "fixture", Port = 8193 });
        if (!connected.Success) throw new InvalidOperationException(connected.ErrorMessage);
        Func<CancellationToken, Task<string[]>>[] browseOperations =
        [
            async ct => (await driver.BrowseFilesAsync("/Programs", ct)).Select(node => node.Path).ToArray(),
            async ct => (await driver.BrowseAsync("/Programs", ct)).Select(node => node.Path).ToArray(),
        ];
        foreach (var browse in browseOperations)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            probe.ProgramDirectoryEntries = [];
            if ((await browse(timeout.Token)).Length != 0)
                throw new InvalidOperationException("A successful empty directory must remain empty");
            foreach (var (code, name) in new[] { (7, "EW_PROT"), (6, "EW_NOOPT"), (-8, "EW_HANDLE") })
            {
                probe.ProgramDirectoryReturnCode = code;
                probe.ProgramDirectoryEntries = [new() { Number = 9001, Size = 32, Comment = "fixture" }];
                var rejected = false;
                try { await browse(timeout.Token); }
                catch (InvalidOperationException error)
                {
                    rejected = error.Message.Contains(nameof(IFocasApi.ReadProgramDirectory))
                        && error.Message.Contains($"{code} ({name})");
                }
                if (!rejected) throw new InvalidOperationException("Directory errors must retain API name and return code");
                probe.ProgramDirectoryReturnCode = 0;
                var recovered = await browse(timeout.Token);
                if (!recovered.SequenceEqual(new[] { "/Programs/O9001" }))
                    throw new InvalidOperationException("Directory retry lost entries or failed to release the driver lock");
            }
        }
        Console.WriteLine("  FOCAS directories: empty success, errors and retry passed for both browse paths");
    }

    private static async Task BufferWaitTerminates(bool cancel)
    {
        foreach (var uploading in new[] { false, true })
        {
            var api = DispatchProxy.Create<IFocasApi, FocasTransferProbeApi>();
            var probe = (FocasTransferProbeApi)(object)api;
            probe.AlwaysBusy = true;
            using var cancellation = new CancellationTokenSource();
            if (cancel) probe.CancelWhenBusy = cancellation;
            await using var driver = new FocasDriver(NullLogger<FocasDriver>.Instance, api);
            await driver.ConnectAsync(new DeviceConnectionConfig
            {
                Host = "fixture", Port = 8193, ReadTimeout = TimeSpan.FromMilliseconds(30),
            });
            using var stream = new MemoryStream(Encoding.ASCII.GetBytes("%\nO9001\nM30\n%"));
            try
            {
                var result = uploading
                    ? await driver.UploadProgramAsync(stream, new NCProgramMetadata
                        { FileName = "O9001.nc", RemotePath = "/Programs/O9001" }, ct: cancellation.Token)
                    : await driver.DownloadProgramAsync("/Programs/O1001", stream, ct: cancellation.Token);
                if (cancel || result.Success || result.ErrorMessage?.Contains("timeout") != true)
                    throw new InvalidOperationException("Busy transfer did not terminate with the expected error");
            }
            catch (OperationCanceledException) when (cancel) { }
            if (probe.FinalizeCalls != 1)
                throw new InvalidOperationException("Interrupted transfer was not finalized exactly once");
            probe.AlwaysBusy = false;
            probe.CancelWhenBusy = null;
            using var retry = new MemoryStream();
            var recovered = await driver.DownloadProgramAsync("/Programs/O1001", retry);
            if (!recovered.Success || retry.Length == 0)
                throw new InvalidOperationException("Driver did not recover after interrupted transfer");
        }
    }

    private static async Task BufferBusyPreservesContents(string path)
    {
        var api = DispatchProxy.Create<IFocasApi, FocasTransferProbeApi>();
        var probe = (FocasTransferProbeApi)(object)api;
        probe.BusySendCalls.UnionWith([1, 3]);
        probe.BusyReceiveCalls.UnionWith([1, 3]);
        await using var driver = new FocasDriver(NullLogger<FocasDriver>.Instance, api);
        await driver.ConnectAsync(new DeviceConnectionConfig { Host = "fixture", Port = 8193 });
        var contents = "%\nO9001\n" + string.Concat(Enumerable.Repeat("G01 X1 Y2 F100\n", 64)) + "M30\n%";
        var expected = Encoding.ASCII.GetBytes(contents);
        using var source = new MemoryStream(expected);
        var upload = await driver.UploadProgramAsync(source, new NCProgramMetadata
        {
            FileName = "O9001.nc", RemotePath = path, FileSize = expected.Length,
        });
        using var destination = new MemoryStream();
        var download = await driver.DownloadProgramAsync(path, destination);
        if (!upload.Success || !download.Success || upload.BytesTransferred != expected.Length
            || !expected.SequenceEqual(destination.ToArray()))
            throw new InvalidOperationException($"Transfer lost contents at {path}: {upload.ErrorMessage}; {download.ErrorMessage}");
    }
}
