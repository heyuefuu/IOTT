namespace MachineConnectionApi.Tests;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MachineConnectionApi.Controllers;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

internal static class EvaluationIndicatorRegressionTests
{
    private static List<EvaluationItem> Items(EvaluationConfig config) => config.Indicators.SelectMany(section => section.Children).SelectMany(child => child.Items).ToList();
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Expect<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}");
    }

    private static void ValidateInvalidPayloads(EvaluationConfig config)
    {
        var first = config.Indicators[0];
        Expect<ArgumentException>(() => EvaluationIndicatorStore.Validate(config with { Category = "../outside" }));
        foreach (var weight in new[] { -1d, 100.1d, double.NaN, double.PositiveInfinity })
            Expect<ArgumentException>(() => EvaluationIndicatorStore.Validate(config with { Indicators = [first with { Weight = weight }] }));
        Expect<ArgumentException>(() => EvaluationIndicatorStore.Validate(config with { Indicators = [first, first] }));
        Expect<ArgumentException>(() => EvaluationIndicatorStore.Validate(config with { Indicators = [first with { Children = null! }] }));
        Expect<ArgumentException>(() => EvaluationIndicatorStore.Validate(config with { Indicators = [first with { Name = " " }] }));
        var child = first.Children[0];
        var item = child.Items[0];
        var unknown = child with { Items = [item with { MetricId = "unknown-automation" }] };
        Expect<ArgumentException>(() => EvaluationIndicatorStore.Validate(config with { Indicators = [first with { Children = [unknown] }] }));
        var duplicate = child with { Items = [item with { MetricId = "max-connections" }, item with { Id = "other-item", MetricId = "max-connections" }] };
        Expect<ArgumentException>(() => EvaluationIndicatorStore.Validate(config with { Indicators = [first with { Children = [duplicate] }] }));
        var invalidRule = item with
        {
            MetricId = "communication-stability",
            Automation = EvaluationAutomationRules.DefaultFor("communication-stability")! with
            { PassRule = new() { Threshold = 99, Unit = "%" } },
        };
        Expect<ArgumentException>(() => EvaluationIndicatorStore.Validate(config with { Indicators = [first with { Children = [child with { Items = [invalidRule] }] }] }));
    }

    private static void RulesAreExplicitAndUnitSafe()
    {
        var cases = new (string Id, double Value, double Score)[]
        {
            ("industrial-protocol", 50, 50), ("industrial-protocol", 100, 100),
            ("communication-stability", 30, 100), ("communication-stability", 20, 80), ("communication-stability", 10, 60), ("communication-stability", 9.9, 0),
            ("max-connections", 100, 100), ("max-connections", 99, 0), ("max-connections", 4, 0), ("max-connections", 1, 0),
            ("transfer-protocol", 2, 100), ("transfer-protocol", 1, 60), ("transfer-protocol", 0, 0),
            ("file-integrity", 100, 100), ("file-integrity", 75, 80), ("file-integrity", 50, 60), ("file-integrity", 49, 0),
            ("transfer-speed", 10, 100), ("transfer-speed", 5, 80), ("transfer-speed", 2, 60), ("transfer-speed", 1.9, 0),
            ("file-size", 200, 100), ("file-size", 10, 80), ("file-size", 1, 60), ("file-size", 0.9, 0),
        };
        foreach (var (id, value, score) in cases)
        {
            var rule = EvaluationAutomationRules.DefaultFor(id)!;
            EvaluationAutomationRules.Validate(new EvaluationItem { MetricId = id, Automation = rule });
            Assert(EvaluationAutomationRules.Score(rule, value) == score, $"Incorrect score for {id} at {value}");
            Assert(EvaluationAutomationRules.Judge(rule, value) == (id == "max-connections" ? value >= 100 : (bool?)null),
                $"Default pass criterion does not match the documented requirement for {id}");
            Assert(EvaluationAutomationRules.Score(rule, 0) == 0, $"A valid zero was lost for {id}");
            foreach (var invalid in new[] { -1d, double.NaN, double.PositiveInfinity })
                Assert(EvaluationAutomationRules.Score(rule, invalid) is null, $"Invalid measurement scored for {id}");
        }
        Assert(EvaluationAutomationRules.DefaultFor("ordinary-item") is null, "Ordinary indicator acquired an automatic binding");
        Assert(EvaluationAutomationRules.Score(EvaluationAutomationRules.DefaultFor("industrial-protocol")!, 246) is null, "Prototype 246% was accepted");
        Assert(EvaluationAutomationRules.Score(EvaluationAutomationRules.DefaultFor("max-connections")!, 1.5) is null, "Fractional connections were accepted");
        var concurrency = EvaluationAutomationRules.DefaultFor("max-connections")!;
        Assert(concurrency.Test.MaxConnections == 100, "Default concurrency test cannot reach the required 100 devices");
        foreach (var maximum in new[] { 1, 64, 65, 100 })
            EvaluationAutomationRules.Validate(new() { MetricId = "max-connections",
                Automation = concurrency with { Test = concurrency.Test with { MaxConnections = maximum } } });

        var speed = EvaluationAutomationRules.DefaultFor("transfer-speed")!;
        var converted = speed with
        {
            Unit = "KiB/s",
            ScoreBands = speed.ScoreBands.Select(band => band with { Min = band.Min * 1024 }).ToList(),
            PassRule = new() { Comparison = "gte", Threshold = 5, Unit = "MB/s" },
        };
        Assert(EvaluationAutomationRules.Score(converted, 5120) == 80 && EvaluationAutomationRules.Judge(converted, 5120) == true,
            "Speed threshold did not convert KiB/s to MB/s");
        Assert(EvaluationAutomationRules.Judge(converted, 5119) == false, "Converted pass threshold was ignored");
        Assert(EvaluationAutomationRules.TryConvert(1024, "KiB", "MiB", out var mib) && mib == 1, "Binary byte conversion changed");
        Assert(!EvaluationAutomationRules.TryConvert(1, "MB", "MB/s", out _), "Incompatible dimensions were converted");
        Assert(!EvaluationAutomationRules.TryConvert(double.MaxValue, "MiB", "B", out _), "Overflowing conversion was accepted");
        var stability = EvaluationAutomationRules.DefaultFor("communication-stability")!;
        var minutes = stability with { PassRule = new() { Comparison = "eq", Threshold = 1800, Unit = "s" } };
        Assert(EvaluationAutomationRules.Judge(minutes, 30) == true && EvaluationAutomationRules.Judge(minutes, 20) == false, "Equality duration conversion failed");
        Assert(EvaluationAutomationRules.Judge(minutes with { PassRule = minutes.PassRule! with { Comparison = "lte" } }, 20) == true, "LTE comparison failed");
        var displayPrecision = stability with { PassRule = new() { Comparison = "eq", Threshold = 3, Unit = "min" } };
        Assert(EvaluationAutomationRules.Judge(displayPrecision, 2.99999) == true, "Equality did not tolerate floating point noise at two-decimal display precision");
        Assert(EvaluationAutomationRules.Judge(displayPrecision, 2.99) == false, "Equality tolerance accepted a visibly different value");
        var displayBands = stability with { ScoreBands = [new() { Min = 3, Score = 100 }, new() { Min = 0, Score = 0 }] };
        Assert(EvaluationAutomationRules.Score(displayBands, 2.99999) == 100, "Band boundary did not use two-decimal display precision");
        var invalidRules = new[]
        {
            stability with { PassRule = new() { Threshold = 99, Unit = "%" } },
            stability with { PassRule = new() { Comparison = "unknown", Threshold = 30, Unit = "min" } },
            stability with { ScoreBands = [new() { Min = 0, Score = 0 }, new() { Min = 0, Score = 100 }] },
            stability with { ScoreBands = [new() { Min = 0, Score = 80 }, new() { Min = 10, Score = 60 }] },
            stability with { ScoreBands = [new() { Min = 0, Score = 101 }] },
            stability with { ScoreBands = [new() { Min = double.NaN, Score = 100 }] },
            stability with { ScoreBands = [null!] },
            stability with { ScoreBands = [] },
            stability with { ScoringMode = "linear" },
            stability with { Test = stability.Test with { DurationMinutes = 0 } },
            stability with { Test = stability.Test with { SampleIntervalSeconds = double.NaN } },
            stability with { Test = stability.Test with { FailureLimit = 0 } },
            stability with { Test = stability.Test with { MaxConnections = 101 } },
            stability with { Test = stability.Test with { ConcurrencyMode = "implicit-all" } },
            stability with { Test = stability.Test with { ReadAddress = "x\n" } },
            stability with { Test = stability.Test with { ReadDataType = "InventedType" } },
            stability with { Test = stability.Test with { TargetDirectory = "/test/../programs" } },
        };
        foreach (var invalid in invalidRules)
        {
            Expect<ArgumentException>(() => EvaluationAutomationRules.Validate(new() { MetricId = "communication-stability", Automation = invalid }));
            Assert(EvaluationAutomationRules.Score(invalid, 30) is null && EvaluationAutomationRules.Judge(invalid, 30) is null, "Invalid rule produced a verdict");
        }
        Expect<ArgumentException>(() => EvaluationAutomationRules.Validate(new() { Automation = speed }));
        Expect<ArgumentException>(() => EvaluationAutomationRules.Validate(new() { MetricId = "communication-stability", Automation = speed }));
        var noBand = speed with { ScoreBands = [new() { Min = 1, Score = 60 }] };
        Assert(EvaluationAutomationRules.Score(noBand, 0) is null, "Uncovered measurement was silently scored zero");
        foreach (var alias in new[] { "OPC UA", "opc-ua", "OPC/OPC UA" })
            Assert(EvaluationAutomationRules.NormalizeProtocol(alias) == "OPC UA", "Protocol alias did not normalize");
        Assert(EvaluationAutomationRules.NormalizeProtocol("Modbus TCP") == "Modbus", "Modbus alias did not normalize");
        Assert(EvaluationAutomationRules.NormalizeProtocol("Fanuc Focas") == "FOCAS", "FOCAS alias did not normalize");
    }

    private static void WeightNormalizationIsBounded(EvaluationIndicatorStore store)
    {
        EvaluationConfig Save(params double[] weights) => store.Save(store.Get("machine") with
        {
            Indicators = weights.Select((weight, index) => new EvaluationSection { Id = $"weight-{index}", Name = $"Weight {index}", Weight = weight }).ToList(),
        });
        foreach (var (input, expected) in new[]
        {
            (new[] { 50d, 49d }, new[] { 50d, 50d }),
            (new[] { 50d, 51d }, new[] { 50d, 50d }),
            (new[] { 50d, 48d }, new[] { 50d, 48d }),
            (new[] { 50d, 52d }, new[] { 50d, 52d }),
            (new[] { 40d, 42d }, new[] { 40d, 42d }),
            (new[] { 60d, 41d, 0d }, new[] { 60d, 41d, 0d }),
        })
            Assert(Save(input).Indicators.Select(section => section.Weight).SequenceEqual(expected), "Weight correction crossed draft or nonnegative boundaries");
        var tree = new EvaluationSection
        {
            Id = "root", Name = "Root", Weight = 99,
            Children = [new() { Id = "child", Name = "Child", Weight = 99, Items = [new() { Id = "item", Name = "Item", Weight = 99 }] }],
        };
        var saved = store.Save(store.Get("machine") with { Indicators = [tree] });
        Assert(saved.Indicators[0].Weight == 100 && saved.Indicators[0].Children[0].Weight == 100 && Items(saved)[0].Weight == 100,
            "Weight normalization did not cover all three levels");
        Assert(tree.Weight == 99 && tree.Children[0].Weight == 99 && tree.Children[0].Items[0].Weight == 99, "Saving mutated an earlier snapshot");
        var repeat = store.Save(saved);
        Assert(JsonSerializer.Serialize(repeat.Indicators) == JsonSerializer.Serialize(saved.Indicators), "Repeated save changed normalized weights");
    }

    private static void MissingFieldsMigrateWithoutReinterpretingHistory(EvaluationConfig machine, EvaluationConfig machining)
    {
        var fileName = $"evaluation-indicators-migration-{Guid.NewGuid():N}.json";
        var path = Path.Combine(AppContext.BaseDirectory, "App_Data", fileName);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var configs = JsonSerializer.SerializeToNode(new[] { machine, machining }, options)!.AsArray();
        var rawItems = configs.SelectMany(config => config!["indicators"]!.AsArray())
            .SelectMany(section => section!["children"]!.AsArray()).SelectMany(child => child!["items"]!.AsArray()).ToList();
        foreach (var item in rawItems) item!.AsObject().Remove("automation");
        var stability = rawItems.First(item => item!["metricId"]?.GetValue<string>() == "communication-stability")!;
        stability["method"] = "自定义旧说明：99%才通过，不是持续分钟";
        stability["scoring"] = new JsonObject { ["100"] = "历史文字不能成为自动判据" };
        // Explicit null is intentional, not a missing field to migrate.
        var disabled = rawItems.First(item => item!["metricId"]?.GetValue<string>() == "industrial-protocol")!;
        disabled["automation"] = null;
        var speed = rawItems.First(item => item!["metricId"]?.GetValue<string>() == "transfer-speed")!;
        speed["automation"] = JsonSerializer.SerializeToNode(EvaluationAutomationRules.DefaultFor("transfer-speed"), options);
        var test = speed["automation"]!["test"]!.AsObject();
        test["durationMinutes"] = 12;
        test.Remove("readAddress"); test.Remove("readDataType"); test.Remove("targetDirectory");
        var concurrency = rawItems.First(item => item!["metricId"]?.GetValue<string>() == "max-connections")!;
        var customRule = EvaluationAutomationRules.DefaultFor("max-connections")! with
        {
            Test = new() { MaxConnections = 4 }, PassRule = null,
            ScoreBands = [new() { Min = 4, Score = 100 }, new() { Min = 0, Score = 0 }],
        };
        concurrency["automation"] = JsonSerializer.SerializeToNode(customRule, options);
        var original = configs.ToJsonString();
        try
        {
            System.IO.File.WriteAllText(path, original);
            var store = new EvaluationIndicatorStore(fileName);
            var migrated = store.Get("machine");
            var items = Items(migrated);
            var migratedStability = items.Single(item => item.MetricId == "communication-stability");
            Assert(migratedStability.Method == stability["method"]!.GetValue<string>() && migratedStability.Scoring["100"] == "历史文字不能成为自动判据",
                "Legacy custom text was overwritten");
            Assert(migratedStability.Automation?.Unit == "min" && migratedStability.Automation.PassRule is null, "Legacy prose became an executable threshold");
            Assert(items.Single(item => item.MetricId == "industrial-protocol").Automation is null, "Explicitly disabled rule was re-enabled");
            Assert(items.Where(item => item.MetricId is null).All(item => item.Automation is null), "Migration bound an ordinary item");
            var keptRule = items.Single(item => item.MetricId == "max-connections").Automation!;
            Assert(keptRule.Test.MaxConnections == 4 && keptRule.PassRule is null
                && EvaluationAutomationRules.Score(keptRule, 4) == 100, "New defaults overwrote a saved custom rule");
            var migratedTest = items.Single(item => item.MetricId == "transfer-speed").Automation!.Test;
            Assert(migratedTest.DurationMinutes == 12 && migratedTest.ReadAddress == "" && migratedTest.ReadDataType == "String" && migratedTest.TargetDirectory == "/",
                "Missing test fields did not inherit defaults or custom settings were lost");
            Assert(migrated.Version == machine.Version && migrated.UpdatedAt == machine.UpdatedAt, "Migration rewrote user version or timestamp");
            var once = System.IO.File.ReadAllText(path);
            _ = new EvaluationIndicatorStore(fileName);
            Assert(System.IO.File.ReadAllText(path) == once, "Migration was not idempotent");
            var roundTrip = JsonSerializer.Deserialize<EvaluationConfig>(JsonSerializer.Serialize(migrated, options), options)!;
            Assert(Items(roundTrip).Single(item => item.MetricId == "transfer-speed").Automation!.Test == migratedTest, "Rule snapshot lost test settings");
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }

    private static async Task ConcurrentSaveHasOneWinner(EvaluationIndicatorStore store)
    {
        var current = store.Get("machine");
        var successes = 0;
        var conflicts = 0;
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            try { store.Save(current); Interlocked.Increment(ref successes); }
            catch (EvaluationConfigConflictException) { Interlocked.Increment(ref conflicts); }
        })));
        Assert(successes == 1 && conflicts == 1, "Concurrent update did not enforce optimistic locking");
    }

    private sealed class GeneratedStream(long length, Action<long>? beforeRead = null) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            beforeRead?.Invoke(_position);
            var count = (int)Math.Min(buffer.Length, length - _position);
            buffer[..count].Clear();
            _position += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Read(buffer.Span);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(count);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // IFormFile.Length is deliberately independent of the source stream to test dishonest declarations.
    private sealed class SyntheticFormFile(long declaredLength, Func<Stream> openStream) : IFormFile
    {
        public string ContentType => "application/octet-stream";
        public string ContentDisposition => "";
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public long Length => declaredLength;
        public string Name => "file";
        public string FileName => "test.nc";
        public Stream OpenReadStream() => openStream();
        public void CopyTo(Stream target) { using var source = OpenReadStream(); source.CopyTo(target); }
        public async Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
        { await using var source = OpenReadStream(); await source.CopyToAsync(target, cancellationToken); }
    }

    private static async Task AttachmentRoundTrip(EvaluationIndicatorStore store)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"evaluation-attachments-test-{Guid.NewGuid():N}");
        var controller = new EvaluationIndicatorsController(store, directory);
        var bytes = Encoding.UTF8.GetBytes("O1000\nG00 X0 Y0\nM30\n");
        using var stream = new MemoryStream(bytes);
        var input = new FormFile(stream, 0, bytes.Length, "file", "../sample.nc");
        try
        {
            var result = await controller.Upload(input, CancellationToken.None);
            var metadata = (result.Result as OkObjectResult)?.Value as EvaluationFile;
            Assert(metadata?.Id is not null && metadata.Name == "sample.nc" && metadata.SizeBytes == bytes.Length, "Attachment metadata mismatch");
            var downloaded = await controller.Download(metadata!.Id!, CancellationToken.None) as PhysicalFileResult;
            Assert(downloaded is not null && System.IO.File.ReadAllBytes(downloaded.FileName).SequenceEqual(bytes), "Attachment bytes changed during upload");
            Assert(await controller.Download("../sample.nc", CancellationToken.None) is NotFoundObjectResult, "Attachment path traversal was accepted");
            foreach (var invalidName in new[] { "sample.exe", "bad\n.nc", "bad:stream.nc" })
            {
                var unsupported = new FormFile(stream, 0, bytes.Length, "file", invalidName);
                Assert((await controller.Upload(unsupported, CancellationToken.None)).Result is BadRequestObjectResult, "Unsafe attachment name was accepted");
            }
            var oversized = new SyntheticFormFile(201L * 1024 * 1024, () => throw new InvalidOperationException("Oversized input must not be opened"));
            Assert((await controller.Upload(oversized, CancellationToken.None)).Result is BadRequestObjectResult, "201 MiB attachment was accepted");

            // Stream generated bytes to a temporary directory; do not allocate a 200 MiB array or contact devices.
            const long maximum = 200L * 1024 * 1024;
            var largest = new SyntheticFormFile(maximum, () => new GeneratedStream(maximum));
            var accepted = (await controller.Upload(largest, CancellationToken.None)).Result as OkObjectResult;
            var largeMetadata = accepted?.Value as EvaluationFile;
            Assert(largeMetadata?.SizeBytes == maximum, "Exact 200 MiB boundary was rejected");
            var largePath = Path.Combine(directory, $"{largeMetadata!.Id}.bin");
            Assert(new FileInfo(largePath).Length == maximum, "Maximum attachment length changed");
            System.IO.File.Delete(largePath);
            System.IO.File.Delete(Path.Combine(directory, $"{largeMetadata.Id}.json"));

            var before = Directory.GetFiles(directory).OrderBy(path => path).ToArray();
            foreach (var (declared, actual) in new[] { (10L, 5L), (5L, 10L), (0L, 0L) })
            {
                var inconsistent = new SyntheticFormFile(declared, () => new GeneratedStream(actual));
                Assert((await controller.Upload(inconsistent, CancellationToken.None)).Result is BadRequestObjectResult, "Mismatched attachment length was accepted");
                Assert(Directory.GetFiles(directory).OrderBy(path => path).SequenceEqual(before), "Rejected upload left partial files");
            }
            using var canceled = new CancellationTokenSource();
            var interrupted = new SyntheticFormFile(128 * 1024, () => new GeneratedStream(128 * 1024, position => { if (position > 0) canceled.Cancel(); }));
            try { await controller.Upload(interrupted, canceled.Token); throw new InvalidOperationException("Canceled upload unexpectedly succeeded"); }
            catch (OperationCanceledException) { }
            Assert(Directory.GetFiles(directory).OrderBy(path => path).SequenceEqual(before), "Canceled upload left binary or metadata fragments");
            var failed = new SyntheticFormFile(128 * 1024, () => new GeneratedStream(128 * 1024, position => { if (position > 0) throw new IOException("Synthetic source failure"); }));
            try { await controller.Upload(failed, CancellationToken.None); throw new InvalidOperationException("Broken source unexpectedly succeeded"); }
            catch (IOException) { }
            Assert(Directory.GetFiles(directory).OrderBy(path => path).SequenceEqual(before), "Failed source left binary or metadata fragments");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    public static async Task RunAll()
    {
        await EvaluationPreparationRegressionTests.RunAll();
        RulesAreExplicitAndUnitSafe();
        var fileName = $"evaluation-indicators-test-{Guid.NewGuid():N}.json";
        var path = Path.Combine(AppContext.BaseDirectory, "App_Data", fileName);
        try
        {
            var store = new EvaluationIndicatorStore(fileName);
            var machine = store.Get("machine");
            var machining = store.Get("machining");
            Assert(machine.Indicators.Count == 3 && Items(machine).Count == 30, "Machine prototype inventory mismatch");
            Assert(machining.Indicators.Count == 4 && Items(machining).Count == 35, "Machining prototype inventory mismatch");
            Assert(Items(machine).Count(item => item.MetricId is not null) == 7, "Automation mapping mismatch");
            Assert(Items(machine).Single(item => item.Id == "i_20").MetricId == "max-connections", "Concurrency indicator mapping mismatch");
            Assert(Items(machine).SelectMany(item => item.Files).All(file => file.Id is null), "Prototype file metadata became downloadable");
            Assert(Items(machine).Count(item => item.Automation is not null) == 7 && Items(machining).Count(item => item.Automation is not null) == 7, "Default rules were not limited to the seven mapped metrics");
            MissingFieldsMigrateWithoutReinterpretingHistory(machine, machining);
            var changed = machine with { Indicators = [machine.Indicators[0] with { Weight = 42 }] };
            var saved = store.Save(changed);
            Assert(saved.Version == 2 && saved.Indicators[0].Weight == 42, "Unbalanced draft was not persisted");
            Assert(new EvaluationIndicatorStore(fileName).Get("machine").Version == 2, "Config did not survive reloading");
            Assert(store.Get("machining").Version == 1, "Saving machine config changed machining config");
            Expect<EvaluationConfigConflictException>(() => store.Save(changed));
            ValidateInvalidPayloads(store.Get("machine"));
            await ConcurrentSaveHasOneWinner(store);
            WeightNormalizationIsBounded(store);
            var empty = store.Save(store.Get("machine") with { Indicators = [] });
            Assert(empty.Indicators.Count == 0, "Empty draft tree was not saved");
            await AttachmentRoundTrip(store);
            System.IO.File.WriteAllText(path, "{broken");
            Expect<JsonException>(() => new EvaluationIndicatorStore(fileName));
            Assert(System.IO.File.ReadAllText(path) == "{broken", "Corrupt data was overwritten");
            System.IO.File.WriteAllText(path, " ");
            Expect<InvalidDataException>(() => new EvaluationIndicatorStore(fileName));
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }
}
