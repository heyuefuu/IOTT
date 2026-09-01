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

    public VerifyTasksController(IVerifyTaskStore store, IVerifyTaskRunner runner)
    {
        _store = store;
        _runner = runner;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<VerifyTaskDto>> List() =>
        Ok(_store.ReadAll().OrderByDescending(x => x.CreatedAt).ToList());

    [HttpPost]
    public ActionResult<VerifyTaskDto> Create([FromBody] VerifyTaskDto input)
    {
        var item = input with
        {
            Id = Guid.NewGuid().ToString("N"),
            Status = string.IsNullOrWhiteSpace(input.Status) ? "pending" : input.Status,
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
        var item = _store.Update<VerifyTaskDto?>(rows =>
        {
            var index = rows.FindIndex(x => x.Id == id);
            if (index < 0) return null;
            // 编辑保存不得抹掉运行留痕（前端提交体不携带这些字段）
            rows[index] = input with
            {
                Id = id,
                CreatedAt = rows[index].CreatedAt,
                LastRunJson = rows[index].LastRunJson,
                LastAutoRunAt = rows[index].LastAutoRunAt,
            };
            return rows[index];
        });
        return item is null ? NotFound() : Ok(item);
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        var removed = _store.Update(rows => rows.RemoveAll(x => x.Id == id) > 0);
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
    }

    /// <summary>
    /// 导出最近一次运行结果为 Excel（.xlsx）。
    /// 按设计方案 2.5：仅导出实测值与指标参考值对比，含空白「人工评分」列，最终评价由人工填写。
    /// </summary>
    [HttpGet("{id}/export")]
    public IActionResult Export(string id)
    {
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
        if (run is null)
            return BadRequest(new { error = "运行结果留痕数据损坏，请重新运行任务" });

        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "CNC 评价验证报告" },
            new object?[] { "任务名称", run.TaskName, "", "运行编号", run.RunId },
            new object?[] { "开始时间", run.StartedAt, "", "完成时间", run.CompletedAt },
            new object?[] { "总体结论", run.Result, "", "说明", run.Detail },
            new object?[] { },
            new object?[] { "指标编码", "指标名称", "实测结果", "结果说明", "指标参考值", "达标标注", "人工评分" },
        };
        foreach (var metric in run.Metrics)
        {
            rows.Add(new object?[]
            {
                metric.Code,
                metric.Name,
                metric.Value,
                metric.Detail,
                metric.Reference,
                metric.Result,
                "", // 人工评分留空，由评审人填写
            });
        }
        rows.Add([]);
        rows.Add(new object?[] { "原始证据（数据留痕）" });
        foreach (var metric in run.Metrics)
        {
            foreach (var evidence in metric.Evidence)
                rows.Add(new object?[] { metric.Code, metric.Name, evidence });
        }

        var bytes = ExcelBuilder.Build("验证报告", rows, columnWidths: [12, 20, 26, 48, 40, 10, 12]);
        var fileName = $"{task.Name}-验证报告-{DateTimeOffset.Now:yyyyMMddHHmmss}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }
}
