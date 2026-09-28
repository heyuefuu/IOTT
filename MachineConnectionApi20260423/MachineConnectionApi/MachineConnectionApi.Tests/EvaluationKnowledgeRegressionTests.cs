using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;

namespace MachineConnectionApi.Tests;

internal static class EvaluationKnowledgeRegressionTests
{
    public static void RunAll()
    {
        using var fixture = new Fixture();
        ScoresVersionsAndSnapshots(fixture);
        SpreadsheetRoundTrip(fixture);
        TaskEvidenceAndDeduplication(fixture);
        MalformedSpreadsheetIsRejected();
        PdfContainsAllPages();
    }

    private static void ScoresVersionsAndSnapshots(Fixture fixture)
    {
        var original = fixture.Record();
        var first = original.Snapshot.Indicators[0].Children[0].Items[0];
        original.Scores[first.Id] = 0;
        var saved = fixture.Store.Create(original with { TotalScore = 999 });
        Check(saved.TotalScore is >= 0 and < 100 && saved.Scores[first.Id] == 0, "zero scores or recomputation failed");
        var nullable = fixture.Record();
        nullable.Scores[first.Id] = null;
        Check(fixture.Store.Create(nullable).TotalScore is null, "incomplete scoring must remain null");
        var zeroWeight = fixture.Record();
        var items = zeroWeight.Snapshot.Indicators[0].Children[0].Items;
        zeroWeight.Weights[first.Id] = 0;
        zeroWeight.Weights[items[1].Id] = items[1].Weight + first.Weight;
        zeroWeight.Scores[first.Id] = null;
        Check(fixture.Store.Create(zeroWeight).TotalScore == 100, "zero weight must remain zero");
        var rounded = fixture.Record();
        rounded.Weights[first.Id] = first.Weight - 0.01;
        Check(fixture.Store.Create(rounded).TotalScore == 100, "rounding tolerance must match editor weighting");
        Expect<ArgumentException>(() => fixture.Store.Create(fixture.Record() with { Snapshot = null! }));
        var changedConfig = fixture.Indicators.Get("machine");
        changedConfig.Indicators[0] = changedConfig.Indicators[0] with { Name = "updated-name" };
        fixture.Indicators.Save(changedConfig);
        var forged = saved with { Snapshot = changedConfig, Conclusion = "updated" };
        var updated = fixture.Store.Update(saved.Id, forged)!;
        Check(updated.Version == 2 && updated.Snapshot.Indicators[0].Name != "updated-name", "historical snapshot changed");
        Expect<KnowledgeConflictException>(() => fixture.Store.Update(saved.Id, saved));
        var bad = fixture.Record();
        bad.CategoryWeights[bad.Snapshot.Indicators[0].Id] = 99;
        var count = fixture.Store.List().Count;
        Expect<ArgumentException>(() => fixture.Store.Create(bad));
        Check(fixture.Store.List().Count == count, "validation persisted an invalid record");
        Expect<ArgumentException>(() => fixture.Store.Create(fixture.Record() with { TestDate = "2026-99-99" }));
    }

    private static void SpreadsheetRoundTrip(Fixture fixture)
    {
        var record = fixture.Record();
        var item = record.Snapshot.Indicators[0].Children[0].Items[0];
        record.Scores[item.Id] = 0;
        record.TestResults[item.Id] = "0";
        record.Remarks[item.Id] = "中文备注，真实结果";
        record = fixture.Store.Create(record);
        using var exported = new MemoryStream(fixture.Store.ExportSpreadsheet(record));
        var result = fixture.Store.Import(exported);
        Check(result.Success == 1 && result.Failed == 0, "spreadsheet import failed");
        var imported = fixture.Store.List().First(row => row.DataSource == "import");
        Check(imported.Scores[item.Id] == 0 && imported.TestResults[item.Id] == "0"
            && imported.Remarks[item.Id] == record.Remarks[item.Id], "spreadsheet lost scores or evidence");
        Check(imported.Snapshot.Version == record.Snapshot.Version, "spreadsheet lost snapshot");
        using var template = new MemoryStream(fixture.Store.Template());
        var templateRows = EvaluationSpreadsheetReader.Read(template);
        var header = templateRows[0].Cast<object?>().ToList();
        var valid = templateRows[1].Cast<object?>().ToList();
        foreach (var (column, value) in new[] { (0, "test"), (1, "no"), (2, "model"), (3, "control"), (4, "part"), (7, "2026-09-28") })
            valid[column] = value;
        var invalid = valid.ToList();
        invalid[7] = "invalid-date";
        using var batch = new MemoryStream(ExcelBuilder.Build("records", [header, valid, invalid]));
        var partial = fixture.Store.Import(batch);
        Check(partial.Total == 2 && partial.Success == 1 && partial.Failed == 1 && partial.Errors[0].StartsWith("第 3 行"), "partial import result invalid");
    }

    private static void TaskEvidenceAndDeduplication(Fixture fixture)
    {
        var config = fixture.Indicators.Get("machine");
        var metric = config.Indicators.SelectMany(section => section.Children).SelectMany(child => child.Items)
            .First(item => item.MetricId is not null);
        var run = new VerifyRunResponse
        {
            RunId = "run-1", TaskId = "task-1", Status = "completed", CompletedAt = "2026-09-28T09:00:00+08:00",
            EvaluationSnapshot = config,
            MachineSnapshot = new EvaluationMachineSnapshot
            {
                Id = "historical-device", Name = "历史机床", DeviceCode = "history-code",
                Model = "history-model", ControlSystem = "history-control",
            },
            Metrics = [new VerifyMetricResult { MetricId = metric.MetricId!, Value = "0", Detail = "measured detail", Evidence = ["real evidence"] }],
        };
        fixture.Tasks.Rows =
        [
            new VerifyTaskDto { Id = "task-1", Name = "task", Type = "machine", DeviceId = "edited-device",
                CreatedAt = DateTimeOffset.UtcNow, LastRunJson = JsonSerializer.Serialize(run, new JsonSerializerOptions(JsonSerializerDefaults.Web)) },
            new VerifyTaskDto { Id = "task-2", Name = "unexecuted", Type = "machine", CreatedAt = DateTimeOffset.UtcNow },
        ];
        var tasks = fixture.Store.ListTasks();
        Check(tasks.Count == 1 && tasks[0].MachineName == "历史机床", "sync listed fake tasks or edited device");
        var draft = fixture.Store.TaskDraft("task-1");
        Check(draft.Scores.Values.All(score => score is null), "sync fabricated evaluation scores");
        Check(draft.TestResults[metric.Id] == "0" && draft.Remarks[metric.Id].Contains("real evidence"), "sync lost raw evidence");
        Check(draft.SyncRunId == "run-1" && draft.MachineNo == "history-code", "sync provenance missing");
        var count = fixture.Store.List().Count;
        fixture.Store.TaskDraft("task-1");
        Check(fixture.Store.List().Count == count, "draft was persisted");
        var complete = draft with { PartName = "part" };
        fixture.Store.Create(complete);
        Expect<KnowledgeConflictException>(() => fixture.Store.Create(complete));
        Expect<ArgumentException>(() => fixture.Store.TaskDraft("task-2"));
        Expect<ArgumentException>(() => fixture.Store.TaskDraft("task-1", "machining"));
        run.EvaluationSnapshot = fixture.Indicators.Get("machining");
        run.RunId = "run-machining";
        fixture.Tasks.Rows[0] = fixture.Tasks.Rows[0] with
        { LastRunJson = JsonSerializer.Serialize(run, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        var machining = fixture.Store.TaskDraft("task-1");
        Check(machining.Category == "machining" && machining.Scores.Count == 35, "draft lost recorded category");
        Check(fixture.Store.ListTasks()[0].Category == "machining", "task category was not retained");
    }

    private static void MalformedSpreadsheetIsRejected()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("xl/workbook.xml").Open());
            writer.Write("<!DOCTYPE a [<!ENTITY x SYSTEM 'file:///never-read'>]><a>&x;</a>");
        }
        buffer.Position = 0;
        Expect<System.Xml.XmlException>(() => EvaluationSpreadsheetReader.Read(buffer));
        using var oversized = new MemoryStream();
        using (var archive = new ZipArchive(oversized, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = archive.CreateEntry("large").Open();
            stream.Write(new byte[21 * 1024 * 1024]);
        }
        oversized.Position = 0;
        Expect<ArgumentException>(() => EvaluationSpreadsheetReader.Read(oversized));
    }

    private static void PdfContainsAllPages()
    {
        var bytes = EvaluationPdfBuilder.Build(Enumerable.Range(0, 120).Select(index => $"评价报告第 {index} 行"));
        var text = Encoding.ASCII.GetString(bytes);
        Check(text.StartsWith("%PDF-1.4") && text.Contains("/Count 3") && text.Contains("/ToUnicode"),
            "PDF pagination or Unicode mapping missing");
        Check(text.Contains(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("评价报告第 119 行"))), "PDF truncated report");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Expect<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _indicatorFile = $"evaluation-test-indicators-{Guid.NewGuid():N}.json";
        private readonly string _recordFile = $"evaluation-test-records-{Guid.NewGuid():N}.json";
        public EvaluationIndicatorStore Indicators { get; }
        public TaskStore Tasks { get; } = new();
        public EvaluationKnowledgeStore Store { get; }

        public Fixture()
        {
            Indicators = new(_indicatorFile);
            Store = new(Indicators, Tasks, new DeviceStoreFake(), _recordFile);
        }

        public KnowledgeRecord Record()
        {
            var config = Indicators.Get("machine");
            return new KnowledgeRecord
            {
                MachineName = "测试机床", MachineNo = "machine-code", MachineModel = "model", ControlSystem = "control",
                PartName = "part", TestDate = "2026-09-28", Snapshot = config,
                Scores = config.Indicators.SelectMany(section => section.Children).SelectMany(child => child.Items)
                    .ToDictionary(item => item.Id, _ => (double?)100),
            };
        }

        public void Dispose()
        {
            foreach (var fileName in new[] { _indicatorFile, _recordFile })
            {
                var path = Path.Combine(AppContext.BaseDirectory, "App_Data", fileName);
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    private sealed class TaskStore : IVerifyTaskStore
    {
        public List<VerifyTaskDto> Rows { get; set; } = [];
        public List<VerifyTaskDto> ReadAll() => Rows;
        public void WriteAll(IEnumerable<VerifyTaskDto> items) => Rows = items.ToList();
        public TResult Update<TResult>(Func<List<VerifyTaskDto>, TResult> update) => update(Rows);
    }

    private sealed class DeviceStoreFake : IDeviceStore
    {
        public List<MachineDeviceDto> ReadAll() => [];
        public void WriteAll(IEnumerable<MachineDeviceDto> items) => throw new NotSupportedException();
        public TResult Update<TResult>(Func<List<MachineDeviceDto>, TResult> update) => throw new NotSupportedException();
    }
}
