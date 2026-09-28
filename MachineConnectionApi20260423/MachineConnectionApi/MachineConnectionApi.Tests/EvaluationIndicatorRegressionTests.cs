namespace MachineConnectionApi.Tests;

using System.Text;
using System.Text.Json;
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

    private static async Task AttachmentRoundTrip(EvaluationIndicatorStore store)
    {
        var controller = new EvaluationIndicatorsController(store);
        var bytes = Encoding.UTF8.GetBytes("O1000\nG00 X0 Y0\nM30\n");
        using var stream = new MemoryStream(bytes);
        var input = new FormFile(stream, 0, bytes.Length, "file", "../sample.nc");
        var result = await controller.Upload(input, CancellationToken.None);
        var metadata = (result.Result as OkObjectResult)?.Value as EvaluationFile;
        Assert(metadata?.Id is not null && metadata.Name == "sample.nc" && metadata.SizeBytes == bytes.Length, "Attachment metadata mismatch");
        var directory = Path.Combine(AppContext.BaseDirectory, "App_Data", "evaluation-attachments");
        try
        {
            var downloaded = await controller.Download(metadata!.Id!, CancellationToken.None) as PhysicalFileResult;
            Assert(downloaded is not null && System.IO.File.ReadAllBytes(downloaded.FileName).SequenceEqual(bytes), "Attachment bytes changed during upload");
            Assert(await controller.Download("../sample.nc", CancellationToken.None) is NotFoundObjectResult, "Attachment path traversal was accepted");
            var unsupported = new FormFile(stream, 0, bytes.Length, "file", "sample.exe");
            Assert((await controller.Upload(unsupported, CancellationToken.None)).Result is BadRequestObjectResult, "Unsupported attachment was accepted");
            var oversized = new FormFile(stream, 0, 101L * 1024 * 1024, "file", "large.nc");
            Assert((await controller.Upload(oversized, CancellationToken.None)).Result is BadRequestObjectResult, "Oversized attachment was accepted");
        }
        finally
        {
            System.IO.File.Delete(Path.Combine(directory, $"{metadata!.Id}.bin"));
            System.IO.File.Delete(Path.Combine(directory, $"{metadata.Id}.json"));
        }
    }

    public static async Task RunAll()
    {
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
            var changed = machine with { Indicators = [machine.Indicators[0] with { Weight = 42 }] };
            var saved = store.Save(changed);
            Assert(saved.Version == 2 && saved.Indicators[0].Weight == 42, "Unbalanced draft was not persisted");
            Assert(new EvaluationIndicatorStore(fileName).Get("machine").Version == 2, "Config did not survive reloading");
            Assert(store.Get("machining").Version == 1, "Saving machine config changed machining config");
            Expect<EvaluationConfigConflictException>(() => store.Save(changed));
            ValidateInvalidPayloads(store.Get("machine"));
            await ConcurrentSaveHasOneWinner(store);
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
