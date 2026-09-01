namespace IndustrialIoT.Host.Controllers;

using IndustrialIoT.Application.DTOs;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Pipeline;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class CollectionController : ControllerBase
{
    private readonly ICollectionPipeline _pipeline;

    public CollectionController(ICollectionPipeline pipeline) => _pipeline = pipeline;

    /// <summary>启动设备数据采集</summary>
    [HttpPost("start")]
    public async Task<ActionResult<string>> StartCollection(
        [FromBody] StartCollectionRequest request,
        CancellationToken ct = default)
    {
        var profile = new DeviceCollectionProfile
        {
            DeviceId = request.DeviceId,
            Groups = request.Groups.Select(g => new CollectionGroupConfig
            {
                GroupName = g.GroupName,
                Interval = TimeSpan.FromMilliseconds(g.IntervalMs),
                Tags = g.Tags.Select(t => new TagReadRequest
                {
                    Address = t.Address,
                    DataType = t.DataType,
                }).ToList(),
            }).ToList(),
        };
        var taskId = await _pipeline.StartCollectionAsync(profile, ct);
        return Ok(new { TaskId = taskId });
    }

    /// <summary>停止采集任务</summary>
    [HttpPost("stop/{taskId}")]
    public async Task<IActionResult> StopCollection(string taskId, CancellationToken ct = default)
    {
        await _pipeline.StopCollectionAsync(taskId, ct);
        return NoContent();
    }

    /// <summary>获取所有活跃采集任务状态</summary>
    [HttpGet("status")]
    public ActionResult<IReadOnlyDictionary<string, CollectionTaskStatus>> GetStatus()
    {
        return Ok(_pipeline.GetActiveTasksSnapshot());
    }
}
