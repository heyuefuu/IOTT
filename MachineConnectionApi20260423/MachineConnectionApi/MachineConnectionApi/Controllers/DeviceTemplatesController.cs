namespace MachineConnectionApi.Controllers;

using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// 设备配置模板（配置继承/复用）与智能推荐（协议/端口）。
/// 模板复用 JsonFileStore（device-templates.json）；推荐顺序：模板 → 现网同型号设备 → 同网段设备 → 内置品牌映射。
/// </summary>
[ApiController]
[Route("api/device-templates")]
public sealed class DeviceTemplatesController : ControllerBase
{
    private static readonly JsonFileStore<DeviceTemplateDto> Store = new("device-templates.json");
    private readonly IDeviceStore _devices;

    public DeviceTemplatesController(IDeviceStore devices) => _devices = devices;

    [HttpGet]
    public ActionResult<IReadOnlyList<DeviceTemplateDto>> List([FromQuery] string? keyword)
    {
        var query = Store.ReadAll().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(keyword))
            query = query.Where(x =>
                x.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.Brand.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.Model.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.Protocol.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return Ok(query.OrderByDescending(x => x.CreatedAt).ToList());
    }

    [HttpPost]
    public ActionResult<DeviceTemplateDto> Create([FromBody] DeviceTemplateDto input)
    {
        var item = input with { Id = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.Now };
        Store.Update(rows => { rows.Add(item); return 0; });
        return Ok(item);
    }

    [HttpPut("{id}")]
    public ActionResult<DeviceTemplateDto> Update(string id, [FromBody] DeviceTemplateDto input)
    {
        var item = Store.Update<DeviceTemplateDto?>(rows =>
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

    /// <summary>按品牌/型号/IP 推荐协议与端口。</summary>
    [HttpGet("recommend")]
    public ActionResult<DeviceRecommendationDto> Recommend(
        [FromQuery] string? brand,
        [FromQuery] string? model,
        [FromQuery] string? ip,
        [FromQuery] string? type)
    {
        var hasBrandModel = !string.IsNullOrWhiteSpace(brand) || !string.IsNullOrWhiteSpace(model);

        if (hasBrandModel)
        {
            var template = Store.ReadAll().FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x.Protocol) && x.Port is > 0 &&
                Matches(x.Brand, brand) && Matches(x.Model, model));
            if (template is not null)
                return Ok(new DeviceRecommendationDto(template.Protocol, template.Port!.Value, "template"));

            var device = _devices.ReadAll().FirstOrDefault(x =>
                x.Port > 0 && Matches(x.Brand, brand) && Matches(x.Model, model));
            if (device is not null)
                return Ok(new DeviceRecommendationDto(device.Protocol, device.Port, "existing-device"));
        }

        if (!string.IsNullOrWhiteSpace(ip))
        {
            var subnet = Subnet(ip);
            if (subnet is not null)
            {
                var sibling = _devices.ReadAll().FirstOrDefault(x => x.Port > 0 && Subnet(x.Host) == subnet);
                if (sibling is not null)
                    return Ok(new DeviceRecommendationDto(sibling.Protocol, sibling.Port, "same-subnet"));
            }
        }

        var builtin = RecommendBuiltin(brand, model, type);
        return builtin is null
            ? NotFound(new { error = "无法推荐协议/端口，请手填或先保存设备模板" })
            : Ok(builtin);
    }

    private static bool Matches(string candidate, string? query)
    {
        var q = query?.Trim();
        if (string.IsNullOrWhiteSpace(q) || string.IsNullOrWhiteSpace(candidate)) return false;
        return candidate.Contains(q, StringComparison.OrdinalIgnoreCase)
            || q.Contains(candidate.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? Subnet(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var parts = host.Trim().Split('.');
        return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.{parts[2]}" : null;
    }

    private static DeviceRecommendationDto? RecommendBuiltin(string? brand, string? model, string? type)
    {
        var text = $"{brand} {model} {type}".ToLowerInvariant();
        if (Contains(text, "siemens", "西门子", "s7", "s-1200", "s-1500", "1200", "1500")) return new("S7", 102, "builtin");
        if (Contains(text, "fanuc", "发那科")) return new("FOCAS", 8193, "builtin");
        if (Contains(text, "omron", "欧姆龙", "cj2", "cp1")) return new("FINS", 9600, "builtin");
        if (Contains(text, "inovance", "汇川")) return new("Modbus", 502, "builtin");
        if (Contains(text, "panasonic", "松下", "mewtocol")) return new("Mewtocol", 5007, "builtin");
        if (Contains(text, "opc")) return new("OPCUA", 4840, "builtin");
        if (Contains(text, "modbus")) return new("Modbus", 502, "builtin");
        return null;
    }

    private static bool Contains(string haystack, params string[] needles)
        => needles.Any(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase));
}
