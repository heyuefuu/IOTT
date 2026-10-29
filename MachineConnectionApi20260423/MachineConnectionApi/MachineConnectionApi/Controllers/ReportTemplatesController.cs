namespace MachineConnectionApi.Controllers;

using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/report-templates")]
public sealed class ReportTemplatesController : ControllerBase
{
    private static readonly ReportTemplateStore Store = ReportTemplateStore.Default;

    [HttpGet]
    public ActionResult<IReadOnlyList<ReportTemplateDto>> List([FromQuery] string? type, [FromQuery] string? keyword)
    {
        var query = Store.ReadAll().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(x => x.Type == type);
        if (!string.IsNullOrWhiteSpace(keyword))
            query = query.Where(x => x.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return Ok(query.OrderByDescending(x => x.CreatedAt).ToList());
    }

    [HttpPost]
    public ActionResult<ReportTemplateDto> Create([FromBody] ReportTemplateDto input)
    {
        try { return Ok(Store.Create(input)); }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
    }

    [HttpPut("{id}")]
    public ActionResult<ReportTemplateDto> Update(string id, [FromBody] ReportTemplateDto input)
    {
        try
        {
            var item = Store.Update(id, input);
            return item is null ? NotFound() : Ok(item);
        }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        var removed = Store.Delete(id);
        return removed ? NoContent() : NotFound();
    }

    [HttpPost("{id}/preview")]
    public ActionResult<TemplatePreviewResponse> Preview(string id)
    {
        try
        {
            var template = Store.Require(id);
            return Ok(new TemplatePreviewResponse(ReportTemplateRenderer.Preview(template.Content)));
        }
        catch (KeyNotFoundException error) { return NotFound(new { error = error.Message }); }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
    }
}
