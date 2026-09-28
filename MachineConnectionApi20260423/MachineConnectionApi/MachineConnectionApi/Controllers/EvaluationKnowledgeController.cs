using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace MachineConnectionApi.Controllers;

[ApiController]
[Route("api/evaluation")]
public sealed class EvaluationKnowledgeController(EvaluationKnowledgeStore store) : ControllerBase
{
    [HttpGet("records")]
    public IActionResult List() => Ok(store.List());

    [HttpGet("records/{id}")]
    public IActionResult Find(string id) => store.Find(id) is { } item ? Ok(item) : NotFound();

    [HttpPost("records")]
    public IActionResult Create([FromBody] KnowledgeRecord input) => Execute(() => Ok(store.Create(input)));

    [HttpPut("records/{id}")]
    public IActionResult Update(string id, [FromBody] KnowledgeRecord input) =>
        Execute(() => store.Update(id, input) is { } item ? Ok(item) : NotFound());

    [HttpDelete("records/{id}")]
    public IActionResult Delete(string id) => store.Delete(id) ? NoContent() : NotFound();

    [HttpGet("tasks")]
    public IActionResult Tasks() => Ok(store.ListTasks());

    [HttpGet("tasks/{id}/draft")]
    public IActionResult Draft(string id, [FromQuery] string? category = null) =>
        Execute(() => Ok(store.TaskDraft(id, category)));

    [HttpGet("records/template")]
    public IActionResult Template([FromQuery] string category = "machine") => Execute(() =>
        File(store.Template(category), SpreadsheetType, "评价记录导入模板.xlsx"));

    [HttpPost("records/import")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 5 * 1024 * 1024)]
    public IActionResult Import(IFormFile file) => Execute(() =>
    {
        if (file is null || file.Length == 0 || file.Length > 5 * 1024 * 1024
            || !Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择不超过 5MB 的 XLSX 文件。");
        using var stream = file.OpenReadStream();
        return Ok(store.Import(stream));
    });

    [HttpGet("records/{id}/export")]
    public IActionResult Export(string id, [FromQuery] string format = "xlsx") => Execute(() =>
    {
        var record = store.Find(id);
        if (record is null) return NotFound();
        if (format == "xlsx") return File(store.ExportSpreadsheet(record), SpreadsheetType, $"评价报告-{record.Id}.xlsx");
        if (format == "pdf") return File(store.ExportPdf(record), "application/pdf", $"评价报告-{record.Id}.pdf");
        throw new ArgumentException("报告格式仅支持 xlsx 或 pdf。");
    });

    private const string SpreadsheetType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private IActionResult Execute(Func<IActionResult> action)
    {
        try { return action(); }
        catch (KnowledgeConflictException error) { return Conflict(new { error = error.Message }); }
        catch (KeyNotFoundException error) { return NotFound(new { error = error.Message }); }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (InvalidDataException) { return BadRequest(new { error = "文件内容损坏或不是有效的 XLSX 文件。" }); }
        catch (System.Xml.XmlException) { return BadRequest(new { error = "Excel 文件包含无效的 XML 内容。" }); }
    }
}
