namespace IndustrialIoT.Protocols.FileTransfer.Tests;

using System.Reflection;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging.Abstractions;
using SMBLibrary;
using SMBLibrary.Client;
using ConnectionState = IndustrialIoT.Domain.Enums.ConnectionState;

internal static class SmbRegressionTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 1000).Select(value => (byte)value).ToArray();

    public static async Task RunAll()
    {
        await Regression.Run("SMB short writes preserve full upload", () => ShortWrites(false));
        await Regression.Run("SMB short writes preserve resumed upload", () => ShortWrites(true));
        await Regression.Run("SMB zero write fails without looping", ZeroWrite);
        await Regression.Run("SMB empty error response fails download", () => DownloadFailure(NTStatus.STATUS_ACCESS_DENIED, false));
        await Regression.Run("SMB early EOF fails known length download", () => DownloadFailure(NTStatus.STATUS_END_OF_FILE, false));
        await Regression.Run("SMB empty success fails known length download", () => DownloadFailure(NTStatus.STATUS_SUCCESS, true));
        await Regression.Run("SMB complete download preserves bytes", DownloadSuccess);
    }

    private static async Task ShortWrites(bool resume)
    {
        await using var fixture = new Fixture();
        fixture.Probe.MaxWrite = 7;
        using var source = new MemoryStream(Payload);
        if (resume) fixture.Probe.Contents.Write(Payload, 0, 17);
        var result = resume
            ? await fixture.Driver.ResumeUploadAsync("resume", "O1001.nc", source, 17)
            : await fixture.Driver.UploadProgramAsync(source, new NCProgramMetadata { FileName = "O1001.nc", RemotePath = "/" });
        Regression.Require(result.Success && result.BytesTransferred == Payload.Length, $"Upload failed: {result.ErrorMessage}");
        Regression.Require(fixture.Probe.Contents.ToArray().SequenceEqual(Payload), "Short write skipped source bytes");
        Regression.Require(fixture.Probe.CloseCalls == 1, "File handle was not closed");
    }

    private static async Task ZeroWrite()
    {
        await using var fixture = new Fixture();
        fixture.Probe.MaxWrite = 0;
        using var source = new MemoryStream(Payload);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await fixture.Driver.UploadProgramAsync(source,
            new NCProgramMetadata { FileName = "O1001.nc", RemotePath = "/" }, ct: cancellation.Token);
        Regression.Require(!result.Success && result.ErrorMessage?.Contains("write", StringComparison.OrdinalIgnoreCase) == true,
            "Zero write was reported successful or looped until cancellation");
    }

    private static async Task DownloadFailure(NTStatus status, bool empty)
    {
        await using var fixture = new Fixture();
        fixture.Probe.ReadStatus = status;
        fixture.Probe.EmptySuccessfulRead = empty;
        fixture.Probe.ExpectedLength = Payload.Length;
        using var destination = new MemoryStream();
        var result = await fixture.Driver.DownloadProgramAsync("O1001.nc", destination);
        Regression.Require(!result.Success, $"Truncated/error download returned success with {result.BytesTransferred} bytes");
        Regression.Require(fixture.Probe.CloseCalls == 1, "Download handle was not closed");
    }

    private static async Task DownloadSuccess()
    {
        await using var fixture = new Fixture();
        fixture.Probe.Contents.Write(Payload);
        using var destination = new MemoryStream();
        var result = await fixture.Driver.DownloadProgramAsync("O1001.nc", destination);
        Regression.Require(result.Success && destination.ToArray().SequenceEqual(Payload), "Full download failed");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public SmbTransferDriver Driver { get; } = new(NullLogger<SmbTransferDriver>.Instance);
        public SmbFileStoreProbe Probe { get; }

        public Fixture()
        {
            var fileStore = DispatchProxy.Create<ISMBFileStore, SmbFileStoreProbe>();
            Probe = (SmbFileStoreProbe)(object)fileStore;
            var client = new SMB2Client();
            Regression.SetField(client, "m_isConnected", true);
            Regression.SetField(Driver, "_smbClient", client);
            Regression.SetField(Driver, "_fileStore", fileStore);
            Regression.SetField(Driver, "_state", ConnectionState.Connected);
        }

        public async ValueTask DisposeAsync()
        {
            await Driver.DisposeAsync();
            Probe.Contents.Dispose();
        }
    }
}
