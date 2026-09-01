namespace MachineConnectionApi.Controllers;

using System.Net;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/report-templates")]
public sealed class ReportTemplatesController : ControllerBase
{
    private static readonly JsonFileStore<ReportTemplateDto> Store = new("report-templates.json");

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
        var item = input with { Id = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.Now };
        Store.Update(rows => { rows.Add(item); return 0; });
        return Ok(item);
    }

    [HttpPut("{id}")]
    public ActionResult<ReportTemplateDto> Update(string id, [FromBody] ReportTemplateDto input)
    {
        var item = Store.Update<ReportTemplateDto?>(rows =>
        {
            var index = rows.FindIndex(x => x.Id == id);
            if (index < 0) return null;
            rows[index] = input with { Id = id, CreatedAt = rows[index].CreatedAt };
            return rows[index];
        });
        return item is null ? NotFound() : Ok(item);
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        var removed = Store.Update(rows => rows.RemoveAll(x => x.Id == id) > 0);
        return removed ? NoContent() : NotFound();
    }

    [HttpPost("{id}/preview")]
    public ActionResult<TemplatePreviewResponse> Preview(string id)
    {
        var template = Store.ReadAll().FirstOrDefault(x => x.Id == id);
        return template is null ? NotFound() : Ok(new TemplatePreviewResponse(RenderMarkdown(template.Content)));
    }

    private static string RenderMarkdown(string content)
    {
        var html = new List<string>();
        var inList = false;
        void CloseList() { if (inList) { html.Add("</ul>"); inList = false; } }
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("### ")) { CloseList(); html.Add($"<h3>{Esc(line[4..])}</h3>"); }
            else if (line.StartsWith("## ")) { CloseList(); html.Add($"<h2>{Esc(line[3..])}</h2>"); }
            else if (line.StartsWith("# ")) { CloseList(); html.Add($"<h1>{Esc(line[2..])}</h1>"); }
            else if (line.StartsWith("- ")) { if (!inList) { html.Add("<ul>"); inList = true; } html.Add($"<li>{Esc(line[2..])}</li>"); }
            else if (!string.IsNullOrWhiteSpace(line)) { CloseList(); html.Add($"<p>{Esc(line)}</p>"); }
            else CloseList();
        }
        CloseList();
        return string.Join("", html);
    }

    private static string Esc(string value) => WebUtility.HtmlEncode(value);
}
