namespace MachineConnectionApi.Controllers;

using System.Diagnostics;
using System.Net.Sockets;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// 数据出口/数据转发配置（管理后台可视化配置多类型数据服务出口）。
/// 配置落 App_Data/data-output-targets.json；提供连通性测试（HTTP 或 TCP）。
/// 注：将配置实际应用到上游 Host 的数据输出分发器为后续项（需 Host 侧运行时应用）。
/// </summary>
[ApiController]
[Route("api/data-output")]
public sealed class DataOutputController : ControllerBase
{
    private static readonly JsonFileStore<DataOutputTargetDto> Store = new("data-output-targets.json");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    [HttpGet]
    public ActionResult<IReadOnlyList<DataOutputTargetDto>> List([FromQuery] string? type, [FromQuery] string? keyword)
    {
        var query = Store.ReadAll().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(type))
            query = query.Where(x => string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(keyword))
            query = query.Where(x => x.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.Host.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.Topic.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return Ok(query.OrderByDescending(x => x.CreatedAt).ToList());
    }

    [HttpPost]
    public ActionResult<DataOutputTargetDto> Create([FromBody] DataOutputTargetDto input)
    {
        var item = input with { Id = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.Now };
        Store.Update(rows => { rows.Add(item); return 0; });
        return Ok(item);
    }

    [HttpPut("{id}")]
    public ActionResult<DataOutputTargetDto> Update(string id, [FromBody] DataOutputTargetDto input)
    {
        var item = Store.Update<DataOutputTargetDto?>(rows =>
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

    /// <summary>连通性测试：HTTP(Url) 或 TCP(Host:Port)。</summary>
    [HttpPost("{id}/test")]
    public async Task<ActionResult<DataOutputTestResult>> Test(string id, CancellationToken ct)
    {
        var target = Store.ReadAll().FirstOrDefault(x => x.Id == id);
        if (target is null) return NotFound();

        var sw = Stopwatch.StartNew();
        try
        {
            if (!string.IsNullOrWhiteSpace(target.Url))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, target.Url);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                sw.Stop();
                var ok = (int)resp.StatusCode < 500;
                return Ok(new DataOutputTestResult(ok, sw.ElapsedMilliseconds,
                    ok ? $"HTTP {(int)resp.StatusCode}" : $"HTTP {(int)resp.StatusCode}（服务端错误）"));
            }

            if (string.IsNullOrWhiteSpace(target.Host) || target.Port is not int port || port <= 0)
            {
                sw.Stop();
                return Ok(new DataOutputTestResult(false, sw.ElapsedMilliseconds, "缺少 Host/Port 或 Url"));
            }

            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(target.Host, port, timeoutCts.Token);
            sw.Stop();
            return Ok(new DataOutputTestResult(true, sw.ElapsedMilliseconds, $"TCP {target.Host}:{port} 可达"));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Ok(new DataOutputTestResult(false, sw.ElapsedMilliseconds, ex.Message));
        }
    }
}
