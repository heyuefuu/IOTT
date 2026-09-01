namespace IndustrialIoT.Host.Controllers;

using IndustrialIoT.Application.Commands;
using IndustrialIoT.Application.DTOs;
using IndustrialIoT.Application.Queries;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Infrastructure.BackgroundServices;
using MediatR;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class DevicesController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IDeviceConnectionPool _pool;

    public DevicesController(IMediator mediator, IDeviceConnectionPool pool)
    {
        _mediator = mediator;
        _pool = pool;
    }

    /// <summary>获取所有设备列表</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DeviceDto>>> GetAll(
        [FromQuery] DeviceType? type = null,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new ListDevicesQuery(type), ct);
        return Ok(result);
    }

    /// <summary>根据 ID 获取设备</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<DeviceDto>> GetById(string id, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetDeviceQuery(id), ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>创建设备</summary>
    [HttpPost]
    public async Task<ActionResult<DeviceDto>> Create(
        [FromBody] CreateDeviceRequest request,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new CreateDeviceCommand(request), ct);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    /// <summary>更新设备</summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<DeviceDto>> Update(
        string id,
        [FromBody] UpdateDeviceRequest request,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new UpdateDeviceCommand(id, request), ct);

        // The pooled connection was built from the old config — drop it so the next
        // operation reconnects with the new host/port/protocol settings
        await _pool.ReleaseAsync(id, ct);

        return Ok(result);
    }

    /// <summary>删除设备</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct = default)
    {
        await _mediator.Send(new DeleteDeviceCommand(id), ct);
        await _pool.ReleaseAsync(id, ct);
        return NoContent();
    }

    /// <summary>测试设备连接</summary>
    [HttpPost("{id}/test-connection")]
    public async Task<ActionResult<ConnectionTestResult>> TestConnection(
        string id,
        CancellationToken ct = default)
    {
        // If a pooled connection is already up, probe it instead of opening (and immediately
        // closing) a second socket — some controllers raise an alarm on that close
        if (_pool.TryGetConnected(id, out var pooled) && pooled is not null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var alive = await pooled.PingAsync(ct);
            sw.Stop();

            if (alive)
            {
                return Ok(new ConnectionTestResult
                {
                    Success = true,
                    Latency = sw.Elapsed,
                });
            }

            // Pooled socket is dead — drop it and fall through to a real connection test
            await _pool.ReleaseAsync(id, ct);
        }

        var result = await _mediator.Send(new TestConnectionCommand(id), ct);
        return Ok(result);
    }
}
