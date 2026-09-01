namespace IndustrialIoT.Protocols.FANUC.Tests;

using System.Collections.Concurrent;
using IndustrialIoT.Protocols.FANUC;
using IndustrialIoT.Protocols.Models;

/// <summary>
/// Wraps <see cref="SimulatedFocasApi"/> and counts how many times each FOCAS call is issued,
/// so tests can assert on round-trip count per batch rather than just on returned values.
/// </summary>
internal sealed class CountingFocasApi : IFocasApi
{
    private readonly SimulatedFocasApi _inner = new();
    private readonly ConcurrentDictionary<string, int> _calls = new();

    public int CallCount(string apiName) => _calls.TryGetValue(apiName, out var n) ? n : 0;

    public int TotalCalls => _calls
        .Where(kv => kv.Key is not (nameof(Connect) or nameof(Disconnect)))
        .Sum(kv => kv.Value);

    public IReadOnlyDictionary<string, int> Snapshot() => _calls.ToDictionary(kv => kv.Key, kv => kv.Value);

    public void Reset() => _calls.Clear();

    private T Count<T>(string apiName, Func<T> call)
    {
        _calls.AddOrUpdate(apiName, 1, (_, n) => n + 1);
        return call();
    }

    // ── Counted: everything reachable from a batch read ──────────────

    public int ReadActPos(int handle, out double[] positions)
    {
        double[] result = [];
        var rc = Count(nameof(ReadActPos), () => _inner.ReadActPos(handle, out result));
        positions = result;
        return rc;
    }

    public int ReadAxisPositions(int handle, out FocasAxisPosition[] positions)
    {
        FocasAxisPosition[] result = [];
        var rc = Count(nameof(ReadAxisPositions), () => _inner.ReadAxisPositions(handle, out result));
        positions = result;
        return rc;
    }

    public int ReadSpindleSpeed(int handle, out int speed)
    {
        var result = 0;
        var rc = Count(nameof(ReadSpindleSpeed), () => _inner.ReadSpindleSpeed(handle, out result));
        speed = result;
        return rc;
    }

    public int ReadActualFeed(int handle, out int feed)
    {
        var result = 0;
        var rc = Count(nameof(ReadActualFeed), () => _inner.ReadActualFeed(handle, out result));
        feed = result;
        return rc;
    }

    public int ReadFeedOverride(int handle, out int overridePercent)
    {
        var result = 0;
        var rc = Count(nameof(ReadFeedOverride), () => _inner.ReadFeedOverride(handle, out result));
        overridePercent = result;
        return rc;
    }

    public int ReadSysInfo(int handle, out FocasSystemInfo info)
    {
        FocasSystemInfo? result = null;
        var rc = Count(nameof(ReadSysInfo), () => _inner.ReadSysInfo(handle, out result!));
        info = result!;
        return rc;
    }

    public int ReadAlarmStatus(int handle, out int statusCode, out string message)
    {
        var code = 0;
        var text = string.Empty;
        var rc = Count(nameof(ReadAlarmStatus), () => _inner.ReadAlarmStatus(handle, out code, out text));
        statusCode = code;
        message = text;
        return rc;
    }

    public int ReadRunStatus(int handle, out int status)
    {
        var result = 0;
        var rc = Count(nameof(ReadRunStatus), () => _inner.ReadRunStatus(handle, out result));
        status = result;
        return rc;
    }

    public int ReadProgramInfo(int handle, out FocasProgramInfo program)
    {
        FocasProgramInfo? result = null;
        var rc = Count(nameof(ReadProgramInfo), () => _inner.ReadProgramInfo(handle, out result!));
        program = result!;
        return rc;
    }

    public int ReadStatusInfo(int handle, out FocasStatusInfo status)
    {
        FocasStatusInfo? result = null;
        var rc = Count(nameof(ReadStatusInfo), () => _inner.ReadStatusInfo(handle, out result!));
        status = result!;
        return rc;
    }

    public int ReadActualFeedRate(int handle, out double feedRate)
    {
        var result = 0d;
        var rc = Count(nameof(ReadActualFeedRate), () => _inner.ReadActualFeedRate(handle, out result));
        feedRate = result;
        return rc;
    }

    public int ReadCommandedFeedRate(int handle, out double feedRate)
    {
        var result = 0d;
        var rc = Count(nameof(ReadCommandedFeedRate), () => _inner.ReadCommandedFeedRate(handle, out result));
        feedRate = result;
        return rc;
    }

    public int ReadToolId(int handle, out int toolId)
    {
        var result = 0;
        var rc = Count(nameof(ReadToolId), () => _inner.ReadToolId(handle, out result));
        toolId = result;
        return rc;
    }

    public int ReadMaxToolGroup(int handle, out int maxGroup)
    {
        var result = 0;
        var rc = Count(nameof(ReadMaxToolGroup), () => _inner.ReadMaxToolGroup(handle, out result));
        maxGroup = result;
        return rc;
    }

    public int ReadMacroVariable(int handle, int number, out double value)
    {
        var result = 0d;
        var rc = Count(nameof(ReadMacroVariable), () => _inner.ReadMacroVariable(handle, number, out result));
        value = result;
        return rc;
    }

    public int ReadParameter(int handle, int number, out int value)
    {
        var result = 0;
        var rc = Count(nameof(ReadParameter), () => _inner.ReadParameter(handle, number, out result));
        value = result;
        return rc;
    }

    public int ReadPmc(int handle, int adrType, int startAddr, int length, byte[] data) =>
        Count(nameof(ReadPmc), () => _inner.ReadPmc(handle, adrType, startAddr, length, data));

    public int ReadProgramBlock(int handle, out string block)
    {
        var result = string.Empty;
        var rc = Count(nameof(ReadProgramBlock), () => _inner.ReadProgramBlock(handle, out result));
        block = result;
        return rc;
    }

    // ── Connection (recorded but excluded from TotalCalls) ────────────

    public int Connect(string host, int port, int timeout, out int handle)
    {
        var result = 0;
        var rc = Count(nameof(Connect), () => _inner.Connect(host, port, timeout, out result));
        handle = result;
        return rc;
    }

    public int Disconnect(int handle) => Count(nameof(Disconnect), () => _inner.Disconnect(handle));

    // ── Not exercised by batch reads — straight passthrough ──────────

    public int WritePmc(int handle, int adrType, int startAddr, int length, byte[] data) =>
        _inner.WritePmc(handle, adrType, startAddr, length, data);

    public int ReadAlarm(int handle, out int alarmNo, out string message) =>
        _inner.ReadAlarm(handle, out alarmNo, out message);

    public int ReadProgramNumber(int handle, out int progNum) =>
        _inner.ReadProgramNumber(handle, out progNum);

    public int ReadProgramName(int handle, out string programName) =>
        _inner.ReadProgramName(handle, out programName);

    public int StartProgramDownload(int handle) => _inner.StartProgramDownload(handle);

    public int DownloadProgramChunk(int handle, byte[] data, int length) =>
        _inner.DownloadProgramChunk(handle, data, length);

    public int EndProgramDownload(int handle) => _inner.EndProgramDownload(handle);

    public int StartProgramDownloadAtPath(int handle, string destinationPath) =>
        _inner.StartProgramDownloadAtPath(handle, destinationPath);

    public int DownloadProgramChunkAtPath(int handle, byte[] data, int length, out int acceptedLength) =>
        _inner.DownloadProgramChunkAtPath(handle, data, length, out acceptedLength);

    public int EndProgramDownloadAtPath(int handle) => _inner.EndProgramDownloadAtPath(handle);

    public int StartProgramUpload(int handle, short programNumber) =>
        _inner.StartProgramUpload(handle, programNumber);

    public int UploadProgramChunk(int handle, byte[] buffer, out int actualLength) =>
        _inner.UploadProgramChunk(handle, buffer, out actualLength);

    public int EndProgramUpload(int handle) => _inner.EndProgramUpload(handle);

    public int StartProgramUploadFromPath(int handle, string sourcePath) =>
        _inner.StartProgramUploadFromPath(handle, sourcePath);

    public int UploadProgramChunkFromPath(int handle, byte[] buffer, out int actualLength) =>
        _inner.UploadProgramChunkFromPath(handle, buffer, out actualLength);

    public int EndProgramUploadFromPath(int handle) => _inner.EndProgramUploadFromPath(handle);

    public int ReadProgramDirectory(int handle, out IReadOnlyList<FocasProgramDirectoryEntry> entries) =>
        _inner.ReadProgramDirectory(handle, out entries);

    // Default interface members — SimulatedFocasApi does not override them, so reach them via IFocasApi
    public bool SupportsProgramBlockRead => ((IFocasApi)_inner).SupportsProgramBlockRead;

    public bool SupportsModalReads => ((IFocasApi)_inner).SupportsModalReads;

    public FocasDetailError? ReadDetailError(int handle) => ((IFocasApi)_inner).ReadDetailError(handle);

    public int ReadCncMemoryDirectory(int handle, string path, out IReadOnlyList<ProgramFileEntry> entries) =>
        _inner.ReadCncMemoryDirectory(handle, path, out entries);
}
