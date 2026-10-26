namespace MachineConnectionApi.Controllers;

using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

/// <summary>设备读写操作历史（审计）与回滚查询。</summary>
[ApiController]
[Route("api/device-operation-logs")]
public sealed class DeviceOperationLogsController(DeviceOperationLogStore store) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<DeviceOperationLogDto>> List(
        [FromQuery] string? deviceId,
        [FromQuery] string? type,
        [FromQuery] string? keyword,
        [FromQuery] int take = 200)
        => Ok(store.Query(deviceId, type, keyword, take));
}
