namespace IndustrialIoT.Protocols.FANUC.Tests;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Guards the per-batch round-trip budget of <see cref="FocasDriver.ReadTagsAsync"/>.
/// <para>
/// A FANUC handle serializes every call, so round trips per batch — not per-call latency — is what
/// decides whether a poll finishes before the next one starts. These tests fail if a tag family
/// stops going through <c>BatchReadCache</c>.
/// </para>
/// </summary>
internal static class FocasBatchReadRegressionTests
{
    public static async Task RunAll()
    {
        await SharedApiCallsAreIssuedOncePerBatch();
        await AxisFamiliesShareOnePositionRoundTrip();
        await RepeatedParametersAndMacrosAreDeduplicated();
        await BatchStillReturnsOneValuePerRequestedTag();
        await FailedTagDoesNotAbortRestOfBatch();
    }

    /// <summary>
    /// The customer-facing case: /CNC/* and /CNC/Fanuc/* configured on the same device must not
    /// double up on cnc_statinfo / cnc_alarm2 / cnc_acts / cnc_sysinfo / cnc_rdprgnum.
    /// </summary>
    private static async Task SharedApiCallsAreIssuedOncePerBatch()
    {
        var (driver, api) = await ConnectAsync();
        await using var _ = driver;

        api.Reset();
        var values = await driver.ReadTagsAsync(Tags(
            // status — both families
            ("/CNC/Status/Mode", DataType.Int16),
            ("/CNC/Path/Execution", DataType.String),
            ("/CNC/Path/ControllerMode", DataType.String),
            ("/CNC/Fanuc/Status/Run", DataType.Int32),
            ("/CNC/Fanuc/Status/Alarm", DataType.Int32),
            ("/CNC/Fanuc/Status/AutoModeCode", DataType.Int32),
            ("/CNC/Fanuc/Status/AutoModeName", DataType.String),
            // alarm — both families
            ("/CNC/Alarm/Active", DataType.Bool),
            ("/CNC/Alarm/Code", DataType.Int32),
            ("/CNC/Alarm/Message", DataType.String),
            ("/CNC/Fanuc/Alarm/Status", DataType.Int32),
            ("/CNC/Fanuc/Alarm/Message", DataType.String),
            // spindle, requested twice
            ("/CNC/Spindle/Speed", DataType.Int32),
            // program — both families
            ("/CNC/Program/Number", DataType.Int32),
            ("/CNC/Fanuc/Program/MainNumber", DataType.Int32),
            ("/CNC/Fanuc/Program/SubNumber", DataType.Int32),
            ("/CNC/Fanuc/Program/Name", DataType.String),
            // system
            ("/CNC/Fanuc/System/MaxAxis", DataType.Int32),
            ("/CNC/Fanuc/System/Series", DataType.String),
            ("/CNC/Fanuc/System/CncTypeName", DataType.String),
            ("/CNC/Fanuc/System/AxisCount", DataType.Int32)));

        AssertAllGood(values, nameof(SharedApiCallsAreIssuedOncePerBatch));
        AssertAtMost(1, api.CallCount("ReadStatusInfo"), "ReadStatusInfo");
        AssertAtMost(1, api.CallCount("ReadAlarmStatus"), "ReadAlarmStatus");
        AssertAtMost(1, api.CallCount("ReadSpindleSpeed"), "ReadSpindleSpeed");
        AssertAtMost(1, api.CallCount("ReadProgramInfo"), "ReadProgramInfo");
        AssertAtMost(1, api.CallCount("ReadSysInfo"), "ReadSysInfo");

        // 21 tags collapsing to 5 distinct round trips
        AssertAtMost(5, api.TotalCalls, "TotalCalls for 21 status/alarm/program/system tags");
        Console.WriteLine($"  {nameof(SharedApiCallsAreIssuedOncePerBatch)}: 21 tags -> {api.TotalCalls} round trips");
    }

    /// <summary>
    /// /CNC/Axis/{n} and /CNC/Fanuc/Axis/{n}/{Absolute|Relative} both resolve to cnc_rdposition;
    /// one batch must issue it once even when both families are configured.
    /// </summary>
    private static async Task AxisFamiliesShareOnePositionRoundTrip()
    {
        var (driver, api) = await ConnectAsync();
        await using var _ = driver;

        api.Reset();
        var values = await driver.ReadTagsAsync(Tags(
            ("/CNC/Axis/X", DataType.Double),
            ("/CNC/Axis/Y", DataType.Double),
            ("/CNC/Axis/Z", DataType.Double),
            ("/CNC/Axis/A", DataType.Double),
            ("/CNC/Axis/B", DataType.Double),
            ("/CNC/Axis/C", DataType.Double),
            ("/CNC/Fanuc/Axis/X/Absolute", DataType.Double),
            ("/CNC/Fanuc/Axis/X/Relative", DataType.Double),
            ("/CNC/Fanuc/Axis/Y/Absolute", DataType.Double),
            ("/CNC/Fanuc/Axis/Y/Relative", DataType.Double),
            ("/CNC/Fanuc/Axis/Z/Absolute", DataType.Double),
            ("/CNC/Fanuc/Axis/Z/Relative", DataType.Double)));

        AssertAllGood(values, nameof(AxisFamiliesShareOnePositionRoundTrip));
        var positionCalls = api.CallCount("ReadAxisPositions") + api.CallCount("ReadActPos");
        AssertAtMost(1, positionCalls, "ReadAxisPositions + ReadActPos");
        Console.WriteLine($"  {nameof(AxisFamiliesShareOnePositionRoundTrip)}: 12 axis tags -> {positionCalls} position round trip(s)");
    }

    /// <summary>
    /// Part-count total (6712) and the three time counters share the parameter API; PowerOn (6750),
    /// Working (6751/6752) and Cutting (6753/6754) are distinct numbers but must each be read once.
    /// </summary>
    private static async Task RepeatedParametersAndMacrosAreDeduplicated()
    {
        var (driver, api) = await ConnectAsync();
        await using var _ = driver;

        api.Reset();
        var values = await driver.ReadTagsAsync(Tags(
            ("/CNC/Fanuc/Time/PowerOnSeconds", DataType.Int32),
            ("/CNC/Fanuc/Time/WorkingSeconds", DataType.Int32),
            ("/CNC/Fanuc/Time/CuttingSeconds", DataType.Int32),
            ("/CNC/Fanuc/Production/PartCountTotal", DataType.Int32),
            ("/CNC/Fanuc/Production/PartCountCurrent", DataType.Int32),
            // duplicates of the above — must not add round trips
            ("/CNC/Fanuc/Time/PowerOnSeconds", DataType.Int32),
            ("/CNC/Fanuc/Production/PartCountTotal", DataType.Int32),
            ("/CNC/Fanuc/Production/PartCountCurrent", DataType.Int32)));

        AssertAllGood(values, nameof(RepeatedParametersAndMacrosAreDeduplicated));
        // distinct parameter numbers: 6750, 6751, 6752, 6753, 6754, 6712 = 6
        AssertAtMost(6, api.CallCount("ReadParameter"), "ReadParameter");
        AssertAtMost(1, api.CallCount("ReadMacroVariable"), "ReadMacroVariable");
        Console.WriteLine($"  {nameof(RepeatedParametersAndMacrosAreDeduplicated)}: 8 tags -> {api.CallCount("ReadParameter")} param + {api.CallCount("ReadMacroVariable")} macro");
    }

    /// <summary>Caching must not change the shape of the result: one value per requested tag, in order.</summary>
    private static async Task BatchStillReturnsOneValuePerRequestedTag()
    {
        var (driver, api) = await ConnectAsync();
        await using var _ = driver;

        api.Reset();
        var requested = Tags(
            ("/CNC/Axis/X", DataType.Double),
            ("/CNC/Spindle/Speed", DataType.Int32),
            ("/CNC/Axis/X", DataType.Double),
            ("/CNC/Status/Running", DataType.Bool),
            ("/CNC/Feed/Actual", DataType.Double),
            ("/CNC/Tool/Id", DataType.Int32),
            ("/CNC/Fanuc/Feed/Actual", DataType.Int32),
            ("/CNC/Fanuc/Feed/Override", DataType.Int32),
            ("/CNC/Fanuc/Tool/MaxGroup", DataType.Int32));

        var values = await driver.ReadTagsAsync(requested);

        AssertEqual(requested.Count, values.Count, "value count");
        for (var i = 0; i < requested.Count; i++)
            AssertEqual(requested[i].Address, values[i].Address, $"address at index {i}");

        // Duplicate /CNC/Axis/X must return the same cached reading, not two different samples
        AssertEqual(values[0].Value, values[2].Value, "duplicate tag consistency");
        AssertAllGood(values, nameof(BatchStillReturnsOneValuePerRequestedTag));

        AssertAtMost(1, api.CallCount("ReadRunStatus"), "ReadRunStatus");
        AssertAtMost(1, api.CallCount("ReadActualFeedRate"), "ReadActualFeedRate");
        AssertAtMost(1, api.CallCount("ReadToolId"), "ReadToolId");
        AssertAtMost(1, api.CallCount("ReadActualFeed"), "ReadActualFeed");
        AssertAtMost(1, api.CallCount("ReadFeedOverride"), "ReadFeedOverride");
        AssertAtMost(1, api.CallCount("ReadMaxToolGroup"), "ReadMaxToolGroup");
        Console.WriteLine($"  {nameof(BatchStillReturnsOneValuePerRequestedTag)}: {requested.Count} tags -> {api.TotalCalls} round trips, order preserved");
    }

    /// <summary>
    /// One bad address must come back as a Bad-quality value carrying its error, while the rest of
    /// the batch still reads — that is what lets the UI show a per-point failure reason.
    /// </summary>
    private static async Task FailedTagDoesNotAbortRestOfBatch()
    {
        var (driver, api) = await ConnectAsync();
        await using var _ = driver;

        api.Reset();
        var values = await driver.ReadTagsAsync(Tags(
            ("/CNC/Axis/X", DataType.Double),
            ("/CNC/NoSuchThing", DataType.Int32),
            ("/CNC/Spindle/Speed", DataType.Int32)));

        AssertEqual(3, values.Count, "value count");
        AssertEqual(TagQuality.Good, values[0].Quality, "good tag quality");
        AssertEqual(TagQuality.Bad, values[1].Quality, "bad tag quality");
        AssertEqual(TagQuality.Good, values[2].Quality, "trailing tag quality");

        if (string.IsNullOrWhiteSpace(values[1].ErrorMessage))
            throw new InvalidOperationException("Bad tag must carry a non-empty ErrorMessage for the UI to display");

        Console.WriteLine($"  {nameof(FailedTagDoesNotAbortRestOfBatch)}: bad tag isolated, error = \"{values[1].ErrorMessage}\"");
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static async Task<(FocasDriver Driver, CountingFocasApi Api)> ConnectAsync()
    {
        var api = new CountingFocasApi();
        var driver = new FocasDriver(NullLogger<FocasDriver>.Instance, api);
        var result = await driver.ConnectAsync(new DeviceConnectionConfig { Host = "127.0.0.1", Port = 8193 });
        if (!result.Success)
            throw new InvalidOperationException($"Simulated connect failed: {result.ErrorMessage}");
        return (driver, api);
    }

    private static List<TagReadRequest> Tags(params (string Address, DataType Type)[] tags) =>
        tags.Select(t => new TagReadRequest { Address = t.Address, DataType = t.Type }).ToList();

    private static void AssertAllGood(IReadOnlyList<TagValue> values, string testName)
    {
        var bad = values.Where(v => v.Quality != TagQuality.Good).ToList();
        if (bad.Count > 0)
            throw new InvalidOperationException(
                $"{testName}: {bad.Count} tag(s) returned Bad quality: " +
                string.Join("; ", bad.Select(v => $"{v.Address} -> {v.ErrorMessage}")));
    }

    private static void AssertAtMost(int expected, int actual, string name)
    {
        if (actual > expected)
            throw new InvalidOperationException(
                $"{name}: expected at most {expected} call(s) per batch, got {actual} — a tag family is bypassing BatchReadCache");
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
    }
}
