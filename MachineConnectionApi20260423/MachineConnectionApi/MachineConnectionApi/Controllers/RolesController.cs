namespace MachineConnectionApi.Controllers;

using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

/// <summary>角色管理（RBAC）：角色→权限映射的增删改查。</summary>
[ApiController]
[Route("api/system/roles")]
public sealed class RolesController : ControllerBase
{
    private readonly IRoleStore _store;
    private readonly ISystemActivityLog _activityLog;

    public RolesController(IRoleStore store, ISystemActivityLog activityLog)
    {
        _store = store;
        _activityLog = activityLog;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<RoleDto>> List() =>
        Ok(_store.ReadAll().OrderBy(r => r.CreatedAt).ToList());

    [HttpPost]
    public ActionResult<RoleDto> Create([FromBody] RoleDto input)
    {
        if (string.IsNullOrWhiteSpace(input.Key) || string.IsNullOrWhiteSpace(input.Name))
            return BadRequest(new { error = "角色标识与名称不能为空" });

        var created = _store.Update<(bool Ok, RoleDto? Item)>(rows =>
        {
            if (rows.Any(r => string.Equals(r.Key, input.Key, StringComparison.OrdinalIgnoreCase)))
                return (false, null);
            var item = input with
            {
                Id = Guid.NewGuid().ToString("N"),
                BuiltIn = false,
                CreatedAt = DateTimeOffset.Now,
            };
            rows.Add(item);
            return (true, item);
        });
        if (!created.Ok || created.Item is null)
            return Conflict(new { error = "角色标识已存在" });

        _activityLog.Write("operation", "创建角色", $"{created.Item.Name}（{string.Join("、", created.Item.Permissions)}）");
        return Ok(created.Item);
    }

    [HttpPut("{id}")]
    public ActionResult<RoleDto> Update(string id, [FromBody] RoleDto input)
    {
        var item = _store.Update<RoleDto?>(rows =>
        {
            var index = rows.FindIndex(r => r.Id == id);
            if (index < 0) return null;
            rows[index] = input with
            {
                Id = id,
                BuiltIn = rows[index].BuiltIn,
                CreatedAt = rows[index].CreatedAt,
            };
            return rows[index];
        });
        if (item is null) return NotFound();
        _activityLog.Write("operation", "更新角色", $"{item.Name}（{string.Join("、", item.Permissions)}）");
        return Ok(item);
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        var result = _store.Update<(bool Found, bool BuiltIn)>(rows =>
        {
            var index = rows.FindIndex(r => r.Id == id);
            if (index < 0) return (false, false);
            if (rows[index].BuiltIn) return (true, true);
            rows.RemoveAt(index);
            return (true, false);
        });
        if (!result.Found) return NotFound();
        if (result.BuiltIn) return BadRequest(new { error = "内置角色不可删除" });
        _activityLog.Write("operation", "删除角色", id);
        return NoContent();
    }
}
