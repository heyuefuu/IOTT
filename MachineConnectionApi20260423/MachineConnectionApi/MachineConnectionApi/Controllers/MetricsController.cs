namespace MachineConnectionApi.Controllers;

using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/metrics")]
public sealed class MetricsController : ControllerBase
{
    private readonly IMetricStore _store;

    public MetricsController(IMetricStore store) => _store = store;

    [HttpGet]
    public ActionResult<IReadOnlyList<MetricDto>> List([FromQuery] string? category, [FromQuery] string? keyword)
    {
        var query = _store.ReadAll().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(x => x.Category == category);
        if (!string.IsNullOrWhiteSpace(keyword))
            query = query.Where(x => x.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.StatusLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return Ok(query.OrderByDescending(x => x.CreatedAt).ToList());
    }

    [HttpPost]
    public ActionResult<MetricDto> Create([FromBody] MetricDto input)
    {
        var item = input with { Id = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.Now };
        _store.Update(rows => { rows.Add(item); return 0; });
        return Ok(item);
    }

    [HttpPut("{id}")]
    public ActionResult<MetricDto> Update(string id, [FromBody] MetricDto input)
    {
        var item = _store.Update<MetricDto?>(rows =>
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
        var removed = _store.Update(rows => rows.RemoveAll(x => x.Id == id) > 0);
        return removed ? NoContent() : NotFound();
    }
}
