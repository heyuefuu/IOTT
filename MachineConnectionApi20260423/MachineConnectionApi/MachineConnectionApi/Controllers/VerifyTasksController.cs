namespace MachineConnectionApi.Controllers;

using System.Text.Json;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/verify/tasks")]
public sealed class VerifyTasksController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IVerifyTaskStore _store;
    private readonly IVerifyTaskRunner _runner;
    private readonly IDeviceStore? _devices;

    public VerifyTasksController(IVerifyTaskStore store, IVerifyTaskRunner runner, IDeviceStore? devices = null)
    {
        _store = store;
        _runner = runner;
        _devices = devices;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<VerifyTaskDto>> List() =>
        Ok(_store.ReadAll().OrderByDescending(x => x.CreatedAt).ToList());

    [HttpPost]
    public ActionResult<VerifyTaskDto> Create([FromBody] VerifyTaskDto input)
    {
        try { VerifyTaskOptionsValidation.Validate(input.Options, _devices); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        var item = input with
        {
            Id = Guid.NewGuid().ToString("N"),
            Status = "pending",
            CurrentRunJson = "",
            ActiveRunId = null,
            CreatedAt = DateTimeOffset.Now,
            CompletedAt = null,
            ExecutionTime = "",
            Result = "",
            Detail = "",
            LastRunJson = "",
            LastAutoRunAt = null,
        };
        _store.Update(rows => { rows.Add(item); return 0; });
        return Ok(item);
    }

    [HttpPut("{id}")]
    public ActionResult<VerifyTaskDto> Update(string id, [FromBody] VerifyTaskDto input)
    {
        try { VerifyTaskOptionsValidation.Validate(input.Options, _devices); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        var running = false;
        var item = _store.Update<VerifyTaskDto?>(rows =>
        {
            var index = rows.FindIndex(x => x.Id == id);
            if (index < 0) return null;
            if (rows[index].Status == "running")
            {
                running = true;
                return null;
            }
            var previous = rows[index];
            var changed = previous.DeviceId != input.DeviceId || previous.MachineId != input.MachineId
                || previous.EvaluationCategory != input.EvaluationCategory || !previous.MetricIds.SequenceEqual(input.MetricIds)
                || JsonSerializer.Serialize(previous.Options, JsonOptions) != JsonSerializer.Serialize(input.Options, JsonOptions);
            // Only the runner may publish execution state/history; client JSON is never evidence.
            rows[index] = input with
            {
                Id = id, CreatedAt = previous.CreatedAt,
                Status = changed ? "pending" : previous.Status,
                CompletedAt = changed ? null : previous.CompletedAt,
                ExecutionTime = changed ? "" : previous.ExecutionTime,
                Result = changed ? "" : previous.Result, Detail = changed ? "" : previous.Detail,
                LastRunJson = previous.LastRunJson, LastAutoRunAt = previous.LastAutoRunAt,
                CurrentRunJson = previous.CurrentRunJson, ActiveRunId = previous.ActiveRunId,
            };
            return rows[index];
        });
        if (running) return Conflict(new { error = "任务正在执行，请完成后再编辑" });
        return item is null ? NotFound() : Ok(item);
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        var running = false;
        var removed = _store.Update(rows =>
        {
            if (rows.Any(task => task.Id == id && task.Status == "running"))
            {
                running = true;
                return false;
            }
            return rows.RemoveAll(x => x.Id == id) > 0;
        });
        if (running) return Conflict(new { error = "任务正在执行，请完成后再删除" });
        return removed ? NoContent() : NotFound();
    }

    [HttpPost("{id}/run")]
    public async Task<ActionResult<VerifyTaskDto>> Run(string id, CancellationToken ct)
    {
        try
        {
            var updated = await _runner.RunTaskAsync(id, "手动", ct);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (VerifyTaskAlreadyRunningException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (VerifyTaskCapacityException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (VerifyTaskPersistenceException ex) { return PersistenceUnavailable(ex); }
    }

    private ObjectResult PersistenceUnavailable(VerifyTaskPersistenceException error) =>
        StatusCode(StatusCodes.Status503ServiceUnavailable,
            new { error = error.Message, taskId = error.TaskId, runId = error.RunId, resultPending = true });

    [HttpPost("recover-results")]
    public async Task<IActionResult> RecoverResults(CancellationToken ct)
    {
        try { await _runner.RetryPendingResultsAsync(ct); return NoContent(); }
        catch (VerifyTaskPersistenceException ex) { return PersistenceUnavailable(ex); }
    }

    [HttpPost("{id}/start")]
    public async Task<ActionResult<VerifyTaskDto>> Start(string id, CancellationToken ct)
    {
        try
        {
            var task = await _runner.StartTaskAsync(id, "手动", ct);
            return task is null ? NotFound() : Accepted(task);
        }
        catch (VerifyTaskAlreadyRunningException ex) { return Conflict(new { error = ex.Message }); }
        catch (VerifyTaskCapacityException ex) { return Conflict(new { error = ex.Message }); }
        catch (VerifyTaskPersistenceException ex) { return PersistenceUnavailable(ex); }
    }

    [HttpPost("start-batch")]
    public async Task<ActionResult<IReadOnlyList<VerifyTaskRunResult>>> StartBatch(
        [FromBody] string[] taskIds, CancellationToken ct)
    {
        if (!ValidBatch(taskIds)) return BadRequest(new { error = "请选择 1–7 个不同的验证任务" });
        var results = await Task.WhenAll(taskIds.Select(async taskId =>
        {
            try
            {
                var task = await _runner.StartTaskAsync(taskId, "手动", ct);
                return new VerifyTaskRunResult(taskId, task, task is null ? "验证任务不存在" : null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return new VerifyTaskRunResult(taskId, null, ex.Message); }
        }));
        return Accepted(results);
    }

    private static bool ValidBatch(string[]? ids) => ids is { Length: >= 1 and <= VerifyTaskRunner.MaxConcurrentTasks }
        && !ids.Any(string.IsNullOrWhiteSpace) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length;

    [HttpPost("run-batch")]
    public async Task<ActionResult<IReadOnlyList<VerifyTaskRunResult>>> RunBatch(
        [FromBody] string[] taskIds, CancellationToken ct)
    {
        if (!ValidBatch(taskIds))
            return BadRequest(new { error = "请选择 1–7 个不同的验证任务" });

        var results = await Task.WhenAll(taskIds.Select(async taskId =>
        {
            try
            {
                var task = await _runner.RunTaskAsync(taskId, "手动", ct);
                return new VerifyTaskRunResult(taskId, task, task is null ? "验证任务不存在" : null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return new VerifyTaskRunResult(taskId, null, ex.Message);
            }
        }));
        return Ok(results);
    }

    /// <summary>导出已结束运行的实测、评分、规则快照和证据；不使用当前编辑后的配置补写历史。</summary>
    [HttpGet("{id}/export")]
    public IActionResult Export(string id, [FromQuery] string format = "xlsx")
    {
        if (format is not ("xlsx" or "pdf")) return BadRequest(new { error = "请选择 Excel 或 PDF 格式" });
        var task = _store.ReadAll().FirstOrDefault(x => x.Id == id);
        if (task is null) return NotFound();
        if (string.IsNullOrWhiteSpace(task.LastRunJson))
            return BadRequest(new { error = "该任务尚未执行过，请先运行任务" });

        VerifyRunResponse? run;
        try
        {
            run = JsonSerializer.Deserialize<VerifyRunResponse>(task.LastRunJson, JsonOptions);
        }
        catch (JsonException)
        {
            run = null;
        }
        if (run is null || run.Metrics is null)
            return BadRequest(new { error = "运行结果留痕数据损坏，请重新运行任务" });
        if (run.Status is "running" or "pending")
            return BadRequest(new { error = "该运行尚未结束，不能作为最终报告导出" });

        string Number(double? value) => value.HasValue && double.IsFinite(value.Value)
            ? value.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : "待评分";
        var intro = new[]
        {
            $"任务名称：{run.TaskName}　运行编号：{run.RunId}",
            $"开始时间：{run.StartedAt}　完成时间：{run.CompletedAt}",
            $"执行状态：{run.Status}　验收结论：{run.Result}",
            $"任务总分：{Number(run.TotalScore)}（所选测试项均分，非综合评价分）",
            $"评分说明：{run.ScoreSummary}",
            $"测试机床：{run.MachineSnapshot?.Name ?? "历史未留痕"}　设备编号：{run.MachineSnapshot?.DeviceCode}",
            $"机床端点：{run.MachineSnapshot?.Host}:{run.MachineSnapshot?.Port}　协议：{run.MachineSnapshot?.Protocol}",
            $"评价指标版本：{run.EvaluationSnapshot?.Version.ToString() ?? "历史未留痕"}",
            $"文件写入许可：{(run.OptionsSnapshot is null ? "历史未留痕" : run.OptionsSnapshot.AllowFileWrites ? "本轮已显式许可" : "未许可（只读）")}",
            $"并发目标：{(run.OptionsSnapshot?.ConcurrentDeviceIds is { Count: > 0 } targets ? string.Join("、", targets) : "未配置")}",
        };
        var summaries = run.Metrics.Select(metric => (IReadOnlyList<string>)new[]
        {
            $"{metric.Code} {metric.Name}",
            metric.Measurement.HasValue ? $"{Number(metric.Measurement)} {metric.Unit}" : metric.Value.Length > 0 ? $"{metric.Value}（无结构化实测值）" : "无可靠实测",
            metric.ExecutionStatus == "pending" && run.CompletedAt.Length > 0 ? "历史未记录" : metric.ExecutionStatus,
            string.IsNullOrWhiteSpace(metric.Result) ? metric.Status : metric.Result,
            Number(metric.Score),
        }).ToList();
        var sources = run.Metrics.Select(metric => (IReadOnlyList<string>)new[]
        {
            $"{metric.Code} {metric.Name}", string.IsNullOrWhiteSpace(metric.MeasurementSource) ? "历史未留痕（不作为本轮实测证据）" : metric.MeasurementSource,
            string.IsNullOrWhiteSpace(metric.ScoreReason) ? "未记录评分依据" : metric.ScoreReason,
        }).ToList();
        var rules = run.Metrics.Select(metric => (IReadOnlyList<string>)new[]
        {
            $"{metric.Code} {metric.Name}", DescribeRule(run, metric.MetricId),
        }).ToList();
        var evidence = run.Metrics.SelectMany(metric => new[]
        {
            (IReadOnlyList<string>)new[] { $"{metric.Code} {metric.Name}", metric.Detail },
        }.Concat((metric.Evidence ?? []).Select(value => (IReadOnlyList<string>)new[] { metric.Code, value }))).ToList();
        var tables = new[]
        {
            new EvaluationPdfTable("分项实测与评分", ["指标", "实测值", "执行状态", "验收判定", "得分"], summaries, [24, 25, 15, 23, 13]),
            new EvaluationPdfTable("数据来源与评分依据", ["指标", "测量来源", "评分依据"], sources, [24, 24, 52]),
            new EvaluationPdfTable("本轮配置判据（达标线与评分档位独立）", ["指标", "配置快照"], rules, [24, 76]),
            new EvaluationPdfTable("原始证据与说明", ["指标", "证据"], evidence, [24, 76]),
        };
        if (format == "pdf")
        {
            var pdf = EvaluationPdfBuilder.BuildReport("CNC 评价验证报告", intro, tables, [run.Detail]);
            return File(pdf, "application/pdf", $"{task.Name}-验证报告.pdf");
        }
        var rows = new List<IReadOnlyList<object?>> { new object?[] { "CNC 评价验证报告" } };
        rows.AddRange(intro.Select(line => (IReadOnlyList<object?>)new object?[] { line }));
        // Retain typed numbers (including the legitimate score 0) in Excel.
        rows.Add(new object?[] { "任务总分", run.TotalScore, "所选测试项均分，非综合评价分；空白为待评分" });
        foreach (var table in tables)
        {
            rows.Add([]);
            rows.Add(new object?[] { table.Title });
            rows.Add(table.Headers.Select(header => (object?)header).ToArray());
            rows.AddRange(table.Rows.Select(row => (IReadOnlyList<object?>)row.Select(value => (object?)value).ToArray()));
        }
        rows.Add(new object?[] { "人工评分（评审填写）", "" });
        rows.Add(new object?[] { "说明", run.Detail });
        var bytes = ExcelBuilder.Build("验证报告", rows, columnWidths: [30, 45, 28, 50, 16]);
        var fileName = $"{task.Name}-验证报告-{DateTimeOffset.Now:yyyyMMddHHmmss}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    private static string DescribeRule(VerifyRunResponse run, string metricId)
    {
        var item = run.EvaluationSnapshot?.Indicators.SelectMany(section => section.Children)
            .SelectMany(section => section.Items).FirstOrDefault(item => item.MetricId == metricId);
        if (item?.Automation is not { } rule) return "历史未保存自动测试规则；不使用旧指标阈值或当前配置补判";
        var pass = rule.PassRule is { } line ? $"{line.Comparison} {line.Threshold} {line.Unit}" : "未配置（不能自动判通过）";
        var scoring = rule.ScoringMode == "linear" ? "linear" : "bands：" + string.Join("；", rule.ScoreBands.Select(band => $"≥{band.Min} → {band.Score}分"));
        var test = rule.Test;
        return $"达标线：{pass}\n评分：{scoring}；单位：{rule.Unit}\n"
            + $"持续{test.DurationMinutes}min，采样间隔{test.SampleIntervalSeconds}s，失败上限{test.FailureLimit}，并发{test.MaxConnections}（{test.ConcurrencyMode}）\n"
            + $"读取地址：{(string.IsNullOrWhiteSpace(test.ReadAddress) ? "回退已配置设备点位；无点位则未评分" : test.ReadAddress)}；类型：{test.ReadDataType}；测试目录：{test.TargetDirectory}\n"
            + $"待评价协议：{string.Join("、", item.Protocols)}\n"
            + $"附件：{string.Join("；", item.Files.Select(file => $"{file.Name}（{file.Id ?? "无真实附件ID"}）"))}";
    }
}
