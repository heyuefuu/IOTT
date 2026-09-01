namespace IndustrialIoT.Host.Controllers;

using IndustrialIoT.Application.DTOs;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/collection-config")]
public class CollectionConfigController : ControllerBase
{
    private readonly ICollectionProfileRepository _repo;
    public CollectionConfigController(ICollectionProfileRepository repo) => _repo = repo;

    /// <summary>获取设备的所有采集配置</summary>
    [HttpGet("{deviceId}")]
    public async Task<ActionResult<IReadOnlyList<CollectionProfileDto>>> GetByDevice(
        string deviceId, CancellationToken ct = default)
    {
        var profiles = await _repo.GetByDeviceIdAsync(deviceId, ct);
        return Ok(profiles.Select(MapToDto).ToList());
    }

    /// <summary>获取单个采集配置（含分组和点位）</summary>
    [HttpGet("profile/{profileId}")]
    public async Task<ActionResult<CollectionProfileDto>> GetProfile(
        string profileId, CancellationToken ct = default)
    {
        var profile = await _repo.GetByIdAsync(profileId, ct);
        return profile is null ? NotFound() : Ok(MapToDto(profile));
    }

    /// <summary>为设备创建采集配置</summary>
    [HttpPost("{deviceId}")]
    public async Task<ActionResult<CollectionProfileDto>> Create(
        string deviceId,
        [FromBody] CreateCollectionProfileRequest request,
        CancellationToken ct = default)
    {
        var profile = new CollectionProfile
        {
            DeviceId = deviceId,
            Name = request.Name,
            Groups = request.Groups.Select(g => new CollectionGroup
            {
                ProfileId = "",
                GroupName = g.GroupName,
                IntervalMs = g.IntervalMs,
                Tags = g.Tags.Select(t => new TagConfig
                {
                    GroupId = "",
                    Address = t.Address,
                    DataType = t.DataType,
                    DisplayName = t.DisplayName,
                    Unit = t.Unit,
                }).ToList(),
            }).ToList(),
        };

        // Fix up foreign keys
        foreach (var group in profile.Groups)
        {
            group.ProfileId = profile.Id;
            foreach (var tag in group.Tags)
                tag.GroupId = group.Id;
        }

        await _repo.AddAsync(profile, ct);
        return CreatedAtAction(nameof(GetProfile), new { profileId = profile.Id }, MapToDto(profile));
    }

    /// <summary>更新采集配置</summary>
    [HttpPut("profile/{profileId}")]
    public async Task<ActionResult<CollectionProfileDto>> Update(
        string profileId,
        [FromBody] UpdateCollectionProfileRequest request,
        CancellationToken ct = default)
    {
        var profile = await _repo.GetByIdAsync(profileId, ct);
        if (profile is null) return NotFound();

        if (request.Name != null) profile.Name = request.Name;
        if (request.IsEnabled.HasValue) profile.IsEnabled = request.IsEnabled.Value;

        if (request.Groups != null)
        {
            profile.Groups = request.Groups.Select(g => new CollectionGroup
            {
                ProfileId = profile.Id,
                GroupName = g.GroupName,
                IntervalMs = g.IntervalMs,
                Tags = g.Tags.Select(t => new TagConfig
                {
                    GroupId = "",
                    Address = t.Address,
                    DataType = t.DataType,
                    DisplayName = t.DisplayName,
                    Unit = t.Unit,
                }).ToList(),
            }).ToList();

            foreach (var group in profile.Groups)
                foreach (var tag in group.Tags)
                    tag.GroupId = group.Id;
        }

        await _repo.UpdateAsync(profile, ct);
        return Ok(MapToDto(profile));
    }

    /// <summary>删除采集配置</summary>
    [HttpDelete("profile/{profileId}")]
    public async Task<IActionResult> Delete(string profileId, CancellationToken ct = default)
    {
        var profile = await _repo.GetByIdAsync(profileId, ct);
        if (profile is null) return NotFound();

        await _repo.DeleteAsync(profileId, ct);
        return NoContent();
    }

    private static CollectionProfileDto MapToDto(CollectionProfile p) => new()
    {
        Id = p.Id,
        DeviceId = p.DeviceId,
        Name = p.Name,
        IsEnabled = p.IsEnabled,
        Groups = p.Groups.Select(g => new CollectionGroupDto
        {
            GroupName = g.GroupName,
            IntervalMs = g.IntervalMs,
            Tags = g.Tags.Select(t => new TagConfigDto
            {
                Address = t.Address,
                DataType = t.DataType,
                DisplayName = t.DisplayName,
                Unit = t.Unit,
            }).ToList(),
        }).ToList(),
    };
}

public record CreateCollectionProfileRequest
{
    public required string Name { get; init; }
    public required IReadOnlyList<CollectionGroupRequest> Groups { get; init; }
}

public record UpdateCollectionProfileRequest
{
    public string? Name { get; init; }
    public bool? IsEnabled { get; init; }
    public IReadOnlyList<CollectionGroupRequest>? Groups { get; init; }
}

public record CollectionGroupRequest
{
    public required string GroupName { get; init; }
    public required int IntervalMs { get; init; }
    public required IReadOnlyList<TagConfigRequest> Tags { get; init; }
}

public record TagConfigRequest
{
    public required string Address { get; init; }
    public required DataType DataType { get; init; }
    public string? DisplayName { get; init; }
    public string? Unit { get; init; }
}
