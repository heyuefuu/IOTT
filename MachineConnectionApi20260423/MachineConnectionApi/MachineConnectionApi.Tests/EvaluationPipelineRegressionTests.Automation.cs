using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MachineConnectionApi.Tests;

internal static partial class EvaluationPipelineRegressionTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly VerifyReadObservation ReadSucceeded = new(true, true, true, "fixture application read succeeded");
    private static readonly VerifyReadObservation ReadFailed = new(true, true, false, "fixture application read failed");
    private static readonly VerifyReadObservation NoReadEvidence = new(false, false, false, "fixture protocol does not support a real read");

    private sealed class FakeClock : IVerifyTestClock
    {
        public TimeSpan Elapsed { get; private set; }
        public Action? OnDelay { get; set; }
        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            OnDelay?.Invoke();
            ct.ThrowIfCancellationRequested();
            Elapsed += delay;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMeasurements : IVerifyMeasurementAdapter
    {
        public Func<VerifyMeasurementTarget, VerifyReadObservation> Read { get; set; } = _ => ReadSucceeded;
        public Func<CancellationToken, Task>? BeforeReadCompletes { get; set; }
        public Func<VerifyMeasurementTarget, VerifyReadObservation> Directory { get; set; } = _ => ReadSucceeded;
        public Func<EvaluationFile, VerifyAttachmentValidation> Validate { get; set; } = _ => new(true, "fixture attachment exists");
        public Func<EvaluationFile, VerifyTransferObservation> Transfer { get; set; } = file => new(true, true, true, true, file.SizeBytes!.Value, 2, "fixture create-only upload and SHA256 readback match");
        public Func<VerifyMeasurementTarget, EvaluationFile, VerifyTransferObservation>? TargetTransfer { get; set; }
        public List<(string Id, string Address, string DataType)> Reads { get; } = [];
        public List<(string Id, string Directory)> Directories { get; } = [];
        public List<EvaluationFile> Validations { get; } = [];
        public List<(string Id, string Directory, string Name, EvaluationFile File)> Transfers { get; } = [];
        public async Task<VerifyReadObservation> ReadAsync(VerifyMeasurementTarget target, string address, string dataType, int timeoutMs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Reads.Add((target.Id, address, dataType));
            var result = Read(target);
            if (BeforeReadCompletes is not null) await BeforeReadCompletes(ct);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return result;
        }
        public Task<VerifyReadObservation> ReadTransferDirectoryAsync(VerifyMeasurementTarget target, string directory, int timeoutMs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Directories.Add((target.Id, directory));
            return Task.FromResult(Directory(target));
        }
        public Task<VerifyAttachmentValidation> ValidateAttachmentAsync(EvaluationFile file, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Validations.Add(file); return Task.FromResult(Validate(file));
        }
        public Task<VerifyTransferObservation> TransferAsync(VerifyMeasurementTarget target, EvaluationFile file, string directory, string remoteName, Action<double> reportFraction, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Transfers.Add((target.Id, directory, remoteName, file));
            reportFraction(0.25);
            reportFraction(0.75);
            return Task.FromResult(TargetTransfer?.Invoke(target, file) ?? Transfer(file));
        }
        public Task<VerifyConnectionsObservation> MeasureConnectionsAsync(VerifyMeasurementTarget target, int count,
            string address, string dataType, int timeoutMs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new VerifyConnectionsObservation(false, false, 0, count, "fixture connections are not configured"));
        }
        public Task<VerifyTransferSnapshot> GetTransferSnapshotAsync(VerifyMeasurementTarget target, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new VerifyTransferSnapshot(false, target.Protocol, "fixture transfer snapshot is not configured"));
        }
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Count++; return route(request, cancellationToken); }
    }

    private sealed class PipelineFixture : IDisposable
    {
        private readonly string _fileName = $"evaluation-engine-{Guid.NewGuid():N}.json";
        public EvaluationIndicatorStore Store { get; }
        public FakeMeasurements Adapter { get; } = new();
        public FakeClock Clock { get; } = new();
        public List<VerifyMeasurementTarget> Targets { get; } =
        [
            Target("fixture-machine", "192.0.2.11"), Target("fixture-2", "192.0.2.12"), Target("fixture-3", "192.0.2.13"), Target("untouched", "192.0.2.14"),
        ];
        public RouteHandler Handler { get; }
        private readonly HttpClient _client;
        public VerifyAutomationService Service { get; }
        public PipelineFixture()
        {
            Store = new EvaluationIndicatorStore(_fileName);
            Handler = new RouteHandler((request, _) =>
            {
                Assert(request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/Devices", "Engine tried an unapproved HTTP operation");
                return Task.FromResult(JsonResponse(Targets));
            });
            _client = new HttpClient(Handler) { BaseAddress = new Uri("http://evaluation-fixture.invalid/") };
            Service = new VerifyAutomationService(new ClientFactory(_client), DispatchProxy.Create<ICsConnectivityService, RejectCsProxy>(),
                new EmptyMetricStore(), new ConfigurationBuilder().Build(), NullLogger<VerifyAutomationService>.Instance, Store, Adapter, Clock);
        }
        public void Change(string metricId, Func<EvaluationItem, EvaluationItem> edit)
        {
            var config = Store.Get("machine");
            Store.Save(config with
            {
                Indicators = config.Indicators.Select(section => section with
                {
                    Children = section.Children.Select(child => child with
                    {
                        Items = child.Items.Select(item => item.MetricId == metricId ? edit(item) : item).ToList(),
                    }).ToList(),
                }).ToList(),
            });
        }
        public void Rule(string metricId, Func<EvaluationAutomationRule, EvaluationAutomationRule> edit) =>
            Change(metricId, item => item with { Automation = edit(item.Automation ?? EvaluationAutomationRules.DefaultFor(metricId)!) });
        public Task<VerifyRunResponse> Run(string[] metrics, VerifyRunOptions? options = null, Action<VerifyRunResponse>? progress = null, CancellationToken ct = default) =>
            Service.RunAsync(new VerifyRunRequest { DeviceId = "fixture-machine", EvaluationCategory = "machine", MetricIds = metrics, Options = options, ReportProgress = progress }, ct);
        public void Dispose()
        {
            _client.Dispose();
            var path = Path.Combine(AppContext.BaseDirectory, "App_Data", _fileName);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static VerifyMeasurementTarget Target(string id = "fixture-machine", string host = "192.0.2.11", string protocol = "ModbusTCP") =>
        new() { Id = id, Name = id, Host = host, Port = 502, Protocol = protocol, Transfer = new() { Protocol = "FTP", Host = host, Port = 21 } };
    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body, WebJson), Encoding.UTF8, "application/json") };
    private static EvaluationFile FixtureFile(double mib) => new() { Id = Guid.NewGuid().ToString("N"), Name = $"fixture-{mib.ToString(System.Globalization.CultureInfo.InvariantCulture)}.nc", SizeBytes = (long)Math.Round(mib * 1024 * 1024), Size = $"{mib} MiB" };

    private static async Task AutomationRulesAndEvidence()
    {
        using var f = new PipelineFixture();
        f.Change("industrial-protocol", item => item with { Protocols = ["ModbusTCP", "Modbus", "OpcUa", "OPC UA"] });
        f.Rule("industrial-protocol", rule => rule with { PassRule = new() { Comparison = "gte", Threshold = 80, Unit = "%" } });
        var result = await f.Run(["industrial-protocol"]);
        var metric = result.Metrics.Single();
        Assert(metric.Measurement == 50 && metric.Score == 50 && metric.Status == "failed", "Protocol aliases, selected denominator, or configured pass rule were ignored");
        Assert(result.Status == "completed" && result.Result == "不通过" && result.TotalScore == 50, "Acceptance failure was confused with execution failure");
        Assert(metric.MeasurementSource == "current-run" && metric.Evidence.Any(x => x.Contains("配置协议")), "Configured and observed protocol sources were not separated");

        f.Adapter.Read = _ => ReadFailed;
        result = await f.Run(["industrial-protocol"]);
        Assert(result.Metrics.Single().Score == 0 && result.TotalScore == 0 && result.Status == "completed", "A legitimate measured zero was treated as missing or an execution error");
        f.Rule("industrial-protocol", rule => rule with { PassRule = new() { Comparison = "eq", Threshold = 0, Unit = "%" } });
        result = await f.Run(["industrial-protocol"]);
        Assert(result.Metrics.Single().Status == "passed" && result.TotalScore == 0, "Pass rule was incorrectly inferred from score");
        f.Rule("industrial-protocol", rule => rule with { PassRule = null });
        result = await f.Run(["industrial-protocol"]);
        Assert(result.Metrics.Single().Status == "unrated" && result.Metrics.Single().Score == 0 && result.Result == "待判定", "An unconfigured acceptance rule became an automatic pass");

        f.Adapter.Read = _ => NoReadEvidence;
        result = await f.Run(["industrial-protocol"]);
        Assert(result.Metrics.Single().Score is null && result.Metrics.Single().Measurement is null && result.TotalScore is null, "No evidence was scored zero");
        Assert(result.Status == "completed" && result.Metrics.Single().ExecutionStatus == "skipped", "An unsupported operation became an execution error");
        f.Adapter.Read = _ => throw new InvalidOperationException("fixture execution defect");
        result = await f.Run(["industrial-protocol"]);
        Assert(result.Status == "failed" && result.Metrics.Single().ExecutionStatus == "error", "A real execution exception was not isolated from acceptance");

        f.Change("industrial-protocol", item => item with { Automation = null });
        var before = f.Adapter.Reads.Count;
        result = await f.Run(["industrial-protocol"]);
        Assert(f.Adapter.Reads.Count == before && result.Metrics.Single().Score is null, "An explicitly absent rule silently used defaults/legacy metrics");
        var httpBefore = f.Handler.Count;
        var rejected = false;
        try { await f.Service.RunAsync(new VerifyRunRequest { MetricIds = ["industrial-protocol"] }, CancellationToken.None); }
        catch (ArgumentException) { rejected = true; }
        Assert(rejected && f.Handler.Count == httpBefore, "Empty DeviceId implicitly expanded to all devices");

        f.Change("transfer-protocol", item => item with { Protocols = ["ftp", "FTP"], Files = [FixtureFile(0.1)] });
        var writes = new VerifyRunOptions { AllowFileWrites = true };
        result = await f.Run(["transfer-protocol"], writes);
        Assert(result.Metrics.Single().Measurement == 1 && result.Metrics.Single().Score == 60, "Transfer protocol count was incorrectly scored as a percentage");
        Assert(EvaluationAutomationRules.Score(EvaluationAutomationRules.DefaultFor("transfer-protocol")!, 2) == 100, "The two-protocol top scoring band was lost");
        result = await f.Run(["industrial-protocol", "transfer-protocol"], writes);
        Assert(result.TotalScore is null && result.Metrics[1].Score == 60, "Task mean silently omitted an unscored selected item");
        f.Adapter.Transfer = _ => new(true, true, false, false, 0, 0, "fixture transfer failed");
        result = await f.Run(["transfer-protocol"], writes);
        Assert(result.Metrics.Single().Score == 0 && result.Metrics.Single().Measurement == 0, "A measured transfer protocol failure did not score zero");
        f.Adapter.Transfer = _ => new(false, false, false, false, 0, 0, "unsupported fixture");
        result = await f.Run(["transfer-protocol"], writes);
        Assert(result.TotalScore is null, "Configured transfer protocols were credited without round-trip evidence");
        Assert(f.Adapter.Directories.Count == 0, "Directory reads were still used for transfer coverage");
    }

    private static async Task StabilityAndProgress()
    {
        using var f = new PipelineFixture();
        f.Change("industrial-protocol", item => item with { Protocols = ["Modbus"] });
        f.Rule("communication-stability", rule => rule with
        {
            Unit = "s", ScoreBands = [new() { Min = 1, Score = 70 }, new() { Min = 0, Score = 0 }],
            PassRule = new() { Comparison = "gte", Threshold = 2, Unit = "s" },
            Test = rule.Test with { DurationMinutes = 0.05, SampleIntervalSeconds = 1, ReadAddress = "holding:0", ReadDataType = "UInt16" },
        });
        var reads = 0;
        f.Adapter.Read = _ => ++reads == 3 ? ReadFailed : ReadSucceeded;
        var snapshots = new List<VerifyRunResponse>();
        var result = await f.Run(["communication-stability", "industrial-protocol"], progress: snapshot =>
        {
            snapshots.Add(snapshot);
            if (snapshots.Count == 1)
                f.Rule("communication-stability", rule => rule with { Test = rule.Test with { DurationMinutes = 60, ReadAddress = "later:point" } });
        });
        var stability = result.Metrics[0];
        Assert(reads == 5 && f.Clock.Elapsed == TimeSpan.FromSeconds(3), "Stability did not obey duration and sampling interval");
        Assert(stability.Measurement == 1 && stability.Score == 70 && stability.Status == "failed", "Longest uninterrupted duration/unit conversion is wrong");
        Assert(stability.Detail.Contains("成功 3，失败 1，中断 1") && stability.Detail.Contains("观察 0.05"), "Sampling success/failure/observation evidence is missing");
        Assert(f.Adapter.Reads.Take(4).All(x => x.Address == "holding:0" && x.DataType == "UInt16"), "Rule point and type were not forwarded to the application read");
        Assert(result.TotalScore == 85 && result.Status == "completed", "Task score is not the simple mean of all selected metrics");
        Assert(snapshots[0].Metrics.All(x => x.ExecutionStatus == "pending") && snapshots[0].ProgressPercent == 0, "Initial pending snapshot was mutated or missing");
        Assert(snapshots.Any(x => x.Metrics[0].ProgressPercent is > 0 and < 100 && x.ProgressPercent is > 0 and < 50), "Per-metric real progress/all-selected denominator is missing");
        Assert(snapshots.Any(x => x.Metrics[0].ExecutionStatus == "completed" && x.Metrics[1].ExecutionStatus == "pending"), "Finished/pending project progress states are missing");
        Assert(result.ProgressPercent == 100 && result.CompletedMetricCount == 2 && result.TotalMetricCount == 2 && result.CurrentMetricId is null, "Final progress was not complete");
        Assert(snapshots.Zip(snapshots.Skip(1)).All(pair => pair.First.ProgressPercent <= pair.Second.ProgressPercent), "Progress moved backwards");

        using var cancelFixture = new PipelineFixture();
        using var cancellation = new CancellationTokenSource();
        cancelFixture.Clock.OnDelay = cancellation.Cancel;
        cancelFixture.Rule("communication-stability", rule => rule with { Test = rule.Test with { DurationMinutes = 0.05, SampleIntervalSeconds = 1 } });
        var cancelled = false;
        try { await cancelFixture.Run(["communication-stability"], ct: cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled && cancelFixture.Adapter.Reads.Count == 1, "Cancellation was converted into a measurement or ignored");
    }

    private static async Task ExplicitDeviceConcurrency()
    {
        using var f = new PipelineFixture();
        f.Rule("max-connections", rule => rule with
        {
            Test = rule.Test with { MaxConnections = 3, FailureLimit = 3, ConcurrencyMode = "devices" },
            ScoreBands = [new() { Min = 3, Score = 100 }, new() { Min = 2, Score = 60 }, new() { Min = 0, Score = 0 }],
        });
        f.Adapter.Read = target => target.Id == "fixture-3" ? ReadFailed : ReadSucceeded;
        var result = await f.Run(["max-connections"], new() { ConcurrentDeviceIds = ["fixture-machine", "fixture-2", "fixture-3"] });
        Assert(result.Metrics.Single().Measurement == 2 && result.Metrics.Single().Score == 60, "Concurrency did not record its last fully successful +1 stage");
        Assert(f.Adapter.Reads.Count == 12 && f.Adapter.Reads.Count(x => x.Id == "fixture-3") == 3, "Concurrency did not stop after three consecutive failures at its failing stage");
        Assert(f.Adapter.Reads.All(x => x.Id != "untouched"), "Concurrency accessed an unselected device");
        Assert(result.Metrics.Single().Evidence.Any(x => x.Contains("3设备并发，第3次")), "Concurrency stage/failure evidence is missing");
        var before = f.Adapter.Reads.Count;
        result = await f.Run(["max-connections"]);
        Assert(f.Adapter.Reads.Count == before && result.TotalScore is null, "Single-device task implicitly tested every configured device");
        result = await f.Run(["max-connections"], new() { ConcurrentDeviceIds = ["fixture-machine", "fixture-2"] });
        Assert(result.Metrics.Single().Measurement == 2 && result.TotalScore is null && result.Metrics.Single().ScoreReason.Contains("下限"), "Insufficient targets falsely established a maximum");
        f.Adapter.Read = _ => ReadFailed;
        before = f.Adapter.Reads.Count;
        result = await f.Run(["max-connections"], new() { ConcurrentDeviceIds = ["fixture-machine", "fixture-2", "fixture-3"] });
        Assert(result.TotalScore == 0 && f.Adapter.Reads.Count == before + 3, "Three measured first-stage failures did not terminate with a legitimate zero");
        f.Targets[1] = Target("fixture-2", "192.0.2.11");
        before = f.Adapter.Reads.Count;
        result = await f.Run(["max-connections"], new() { ConcurrentDeviceIds = ["fixture-machine", "fixture-2"] });
        Assert(result.TotalScore is null && f.Adapter.Reads.Count == before, "Duplicate endpoints were counted as distinct target devices");
    }

    private static async Task HundredConcurrentApplicationReads()
    {
        using var fixture = new PipelineFixture();
        fixture.Targets.Clear();
        fixture.Targets.AddRange(Enumerable.Range(1, 100).Select(index =>
            Target(index == 1 ? "fixture-machine" : $"fixture-{index}", $"192.0.2.{index}")));
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalStageReads = 0;
        fixture.Adapter.BeforeReadCompletes = async ct =>
        {
            // The first 99 stages perform 4950 reads; hold every read in stage 100 until all arrive.
            if (fixture.Adapter.Reads.Count <= 4950) return;
            if (Interlocked.Increment(ref finalStageReads) == 100) allStarted.TrySetResult();
            await allStarted.Task.WaitAsync(ct);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await fixture.Run(["max-connections"],
            new() { ConcurrentDeviceIds = fixture.Targets.Select(target => target.Id).ToList() }, ct: deadline.Token);
        Assert(finalStageReads == 100 && fixture.Adapter.Reads.Count == 5050,
            "Default evaluation did not dispatch all 100 application reads concurrently");
        Assert(result.Metrics.Single().Measurement == 100 && result.Metrics.Single().Score == 100,
            "A completed 100-device stage did not meet the documented default rule");
    }

    private static async Task DirectRunAndConnectionModeContracts()
    {
        using var f = new PipelineFixture();
        var controller = new MachineConnectionApi.Controllers.VerifyController(f.Service);
        var invalid = await controller.Run(new VerifyRunRequest { MetricIds = ["industrial-protocol"] }, CancellationToken.None);
        Assert(invalid.Result is Microsoft.AspNetCore.Mvc.BadRequestObjectResult && f.Handler.Count == 0,
            "Direct run must return a validation error before device access when DeviceId is empty");
        foreach (var mode in new[] { "connections", "sessions" })
        {
            f.Rule("max-connections", rule => rule with { Test = rule.Test with { ConcurrencyMode = mode } });
            var before = f.Adapter.Reads.Count;
            var unsupported = await f.Run(["max-connections"]);
            Assert(unsupported.Status == "completed" && unsupported.Metrics.Single().ExecutionStatus == "skipped"
                && unsupported.TotalScore is null && unsupported.Metrics.Single().ScoreReason.Contains(mode)
                && f.Adapter.Reads.Count == before, "Independent connection mode must be configurable but never fake an application-read connection count");
        }
        var options = new VerifyRunOptions { AllowFileWrites = true, ConcurrentDeviceIds = ["fixture-machine"] };
        var snapshots = new List<VerifyRunResponse>();
        var result = await f.Run(["industrial-protocol"], options, snapshot =>
        {
            snapshots.Add(snapshot);
            options.AllowFileWrites = false;
            options.ConcurrentDeviceIds = [];
        });
        Assert(result.OptionsSnapshot?.AllowFileWrites == true && result.OptionsSnapshot.ConcurrentDeviceIds!.SequenceEqual(["fixture-machine"])
            && snapshots.All(snapshot => snapshot.OptionsSnapshot?.AllowFileWrites == true),
            "Direct runs must freeze authorization and explicit targets just like task-runner reports");
    }

    private static async Task FileMeasurements()
    {
        using var f = new PipelineFixture();
        foreach (var id in new[] { "file-integrity", "transfer-speed", "file-size" })
            f.Rule(id, rule => rule with { Test = rule.Test with { TargetDirectory = "/evaluation-fixture" } });
        f.Change("file-integrity", item => item with { Files = [FixtureFile(0.1), FixtureFile(1), FixtureFile(10), FixtureFile(20)] });
        f.Change("transfer-speed", item => item with { Files = [FixtureFile(10)] });
        f.Change("file-size", item => item with { Files = [FixtureFile(1), FixtureFile(10), FixtureFile(200)] });
        var result = await f.Run(["file-integrity", "transfer-speed", "file-size"]);
        Assert(result.TotalScore is null && f.Adapter.Validations.Count == 0 && f.Adapter.Transfers.Count == 0, "Unauthorized file tests performed device writes or attachment I/O");
        var authorized = new VerifyRunOptions { AllowFileWrites = true };
        f.Adapter.Validate = _ => new(false, "fixture missing real attachment");
        result = await f.Run(["file-integrity"], authorized);
        Assert(f.Adapter.Transfers.Count == 0 && result.TotalScore is null && result.Metrics.Single().Evidence.Count == 4, "Missing attachment was replaced or historical evidence was used");
        f.Adapter.Validate = _ => new(true, "fixture exists");
        var snapshots = new List<VerifyRunResponse>();
        result = await f.Run(["file-integrity", "transfer-speed", "file-size"], authorized, snapshots.Add);
        Assert(result.Metrics[0].Measurement == 100 && result.Metrics[0].Score == 100, "Four-size integrity measurement did not use all real configured fixtures");
        Assert(result.Metrics[1].Measurement == 5 && result.Metrics[1].Score == 80, "Speed did not use this run's 10 MiB byte count and upload duration");
        Assert(result.Metrics[2].Measurement == 200 && result.Metrics[2].Score == 100 && result.TotalScore == 93.33, "File-size stages/simple selected mean are wrong");
        Assert(f.Adapter.Transfers.Count == 8 && f.Adapter.Transfers.All(x => x.Id == "fixture-machine" && x.Directory == "/evaluation-fixture"), "File stages/selected target/configured directory were ignored");
        Assert(f.Adapter.Transfers.Select(x => x.Name).Distinct().Count() == 8 && f.Adapter.Transfers.All(x => x.Name.StartsWith("verify-") && x.Name.EndsWith(".nc")), "Remote test filenames were reused or unsafe");
        Assert(snapshots.Any(x => x.Metrics[1].ProgressPercent is > 0 and < 100), "File adapter byte/stage progress was dropped");
        f.Adapter.Transfer = file => new(true, true, true, file.SizeBytes!.Value <= 1024 * 1024, file.SizeBytes.Value, 2, "fixture two readback hashes mismatch");
        result = await f.Run(["file-integrity"], authorized);
        Assert(result.Metrics.Single().Measurement == 50 && result.Metrics.Single().Score == 60, "Integrity did not count failed current-run hash comparisons across all four fixtures");

        f.Adapter.Transfer = file => new(true, true, false, false, 0, 1, "fixture measured transfer failure");
        result = await f.Run(["transfer-speed"], authorized);
        Assert(result.Metrics.Single().Score == 0 && result.Status == "completed", "Measured file failure was not a valid completed zero score");
        f.Adapter.Transfer = file => new(true, true, true, true, file.SizeBytes!.Value, 0, "fixture has no timing");
        result = await f.Run(["transfer-speed"], authorized);
        Assert(result.TotalScore is null, "Zero/missing upload duration was fabricated into a speed");
        f.Adapter.Transfer = file => new(true, true, true, true, 0, 2, "fixture contradictory uploaded bytes");
        result = await f.Run(["file-integrity"], authorized);
        Assert(result.TotalScore is null, "A success flag with contradictory transferred byte count was credited as integrity evidence");
        f.Adapter.Transfer = file => file.SizeBytes == 200L * 1024 * 1024
            ? new(true, true, false, false, 0, 1, "fixture largest size failed")
            : new(true, true, true, true, file.SizeBytes!.Value, 2, "fixture verified round trip");
        result = await f.Run(["file-size"], authorized);
        Assert(result.Metrics.Single().Measurement == 10 && result.Metrics.Single().Score == 80, "Largest successful attachment was not retained after a measured failed stage");
        f.Adapter.Transfer = file => new(false, false, false, false, 0, 0, "fixture cannot safely upload");
        result = await f.Run(["file-integrity"], authorized);
        Assert(result.TotalScore is null && result.Metrics.Single().Measurement is null, "Unsupported writes became zero-scored failures");
        f.Change("file-size", item => item with { Files = [new() { Name = "default-only.nc" }] });
        var before = f.Adapter.Transfers.Count;
        result = await f.Run(["file-size"], authorized);
        Assert(result.TotalScore is null && f.Adapter.Transfers.Count == before, "Default filename metadata created a substitute upload");
    }
}
