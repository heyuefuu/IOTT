namespace IndustrialIoT.Host.Controllers;

using IndustrialIoT.Application.DTOs;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Infrastructure.Messaging;
using IndustrialIoT.Messaging;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Pipeline;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class CollectionController : ControllerBase
{
    private readonly ICollectionPipeline _pipeline;
    private readonly MqttDataOutput? _mqttOutput;
    private readonly IDeviceRepository _deviceRepo;

    public CollectionController(ICollectionPipeline pipeline, IDeviceRepository deviceRepo, MqttDataOutput? mqttOutput = null)
    {
        _pipeline = pipeline;
        _deviceRepo = deviceRepo;
        _mqttOutput = mqttOutput;
    }

    /// <summary>MQTT 转发状态；成功批次和失败尝试次数从进程启动起统计</summary>
    [HttpGet("mqtt/status")]
    public ActionResult<MqttForwardingStatus> GetMqttStatus()
    {
        if (_mqttOutput is null) return StatusCode(503, new { message = "MQTT output is not registered." });
        return Ok(_mqttOutput.GetStatus());
    }

    /// <summary>启动设备数据采集</summary>
    [HttpPost("start")]
    public async Task<ActionResult<string>> StartCollection(
        [FromBody] StartCollectionRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceId))
            return BadRequest(new { error = "deviceId 必填" });

        // 设备可能尚未从上游（网关）同步落库：此处快速失败并给出明确原因，
        // 避免先返回 200+taskId，随后在后台以 "Device ... not found" 异步 fault。
        if (await _deviceRepo.GetByIdAsync(request.DeviceId.Trim(), ct) is null)
            return NotFound(new { error = $"设备不存在或尚未同步到 Host: {request.DeviceId}" });

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
