namespace MachineConnectionApi.Controllers;

using IndustrialIoT.Security;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

public sealed partial class SystemUsersController
{
    [HttpGet("scope-devices")]
    public IActionResult ScopeDevices([FromServices] IDeviceStore devices) =>
        Ok(devices.ReadAll().Select(device => new { device.Id, device.Name, device.Type }));

    [HttpGet("role-scopes")]
    public IActionResult RoleScopes() => Ok(RoleDataScopeStore.List());

    [HttpPut("role-scopes/{role}")]
    public IActionResult SetRoleScope(string role, [FromBody] DeviceDataScope scope)
    {
        try { RoleDataScopeStore.Save(role, scope); }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        foreach (var user in _store.ReadAll().Where(user => user.Role == role && user.DataScope is null))
            _auth.RevokeUserSessions(user.Id);
        _activityLog.Write("operation", "更新角色数据范围", $"{role}：{scope.Mode}，{scope.DeviceIds.Count} 台设备");
        return Ok(new RoleDataScope(role, scope));
    }
}
