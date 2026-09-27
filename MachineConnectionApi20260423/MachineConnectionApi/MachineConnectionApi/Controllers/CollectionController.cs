using Microsoft.AspNetCore.Mvc;
using MachineConnectionApi.Proxy;
using MachineConnectionApi.Services;
using System.Text.Json;
using System.Collections.Concurrent;

namespace MachineConnectionApi.Controllers;

/// <summary>
/// 数据采集 API：采集配置、采集任务启停、点位批量导入，全部转发至 Industrial IoT。
/// </summary>
[ApiController]
[Route("api")]
public class CollectionController : IndustrialIoTProxyControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IDeviceStore _devices;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ImportLocks = new(StringComparer.OrdinalIgnoreCase);

    public CollectionController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<CollectionController> logger,
        IDeviceStore devices)
        : base(httpClientFactory, logger)
    {
        _configuration = configuration;
        _devices = devices;
    }

    private string CollectionConfigPath =>
        _configuration["IndustrialIoT:CollectionConfigPath"] ?? "api/collection-config";

    private string CollectionPath =>
        _configuration["IndustrialIoT:CollectionPath"] ?? "api/collection";

    private string BatchImportPath =>
        _configuration["IndustrialIoT:BatchImportPath"] ?? "api/batch-import";

    /// <summary>获取设备的全部采集配置</summary>
    [HttpGet("collection-config/{deviceId}")]
    public Task<IActionResult> ListProfiles(string deviceId, CancellationToken ct) =>
        ProxyForwardAsync(
            HttpMethod.Get,
            $"{CollectionConfigPath}/{Uri.EscapeDataString(deviceId)}",
            ct);

    /// <summary>获取单个采集配置（含分组和点位）</summary>
    [HttpGet("collection-config/profile/{profileId}")]
    public Task<IActionResult> GetProfile(string profileId, CancellationToken ct) =>
        ProxyForwardAsync(
            HttpMethod.Get,
            $"{CollectionConfigPath}/profile/{Uri.EscapeDataString(profileId)}",
            ct);

    /// <summary>为设备创建采集配置</summary>
    [HttpPost("collection-config/{deviceId}")]
    public Task<IActionResult> CreateProfile(string deviceId, CancellationToken ct) =>
        ProxyForwardAsync(
            HttpMethod.Post,
            $"{CollectionConfigPath}/{Uri.EscapeDataString(deviceId)}",
            ct);

    /// <summary>更新采集配置</summary>
    [HttpPut("collection-config/profile/{profileId}")]
    public Task<IActionResult> UpdateProfile(string profileId, CancellationToken ct) =>
        ProxyForwardAsync(
            HttpMethod.Put,
            $"{CollectionConfigPath}/profile/{Uri.EscapeDataString(profileId)}",
            ct);

    /// <summary>删除采集配置</summary>
    [HttpDelete("collection-config/profile/{profileId}")]
    public Task<IActionResult> DeleteProfile(string profileId, CancellationToken ct) =>
        ProxyForwardAsync(
            HttpMethod.Delete,
            $"{CollectionConfigPath}/profile/{Uri.EscapeDataString(profileId)}",
            ct);

    /// <summary>启动设备数据采集任务</summary>
    [HttpPost("collection/start")]
    public Task<IActionResult> Start(CancellationToken ct) =>
        ProxyForwardAsync(HttpMethod.Post, $"{CollectionPath}/start", ct);

    /// <summary>停止采集任务</summary>
    [HttpPost("collection/stop/{taskId}")]
    public Task<IActionResult> Stop(string taskId, CancellationToken ct) =>
        ProxyForwardAsync(
            HttpMethod.Post,
            $"{CollectionPath}/stop/{Uri.EscapeDataString(taskId)}",
            ct);

    /// <summary>所有活跃采集任务状态</summary>
    [HttpGet("collection/status")]
    public Task<IActionResult> Status(CancellationToken ct) =>
        ProxyForwardAsync(HttpMethod.Get, $"{CollectionPath}/status", ct);

    /// <summary>批量导入采集点位（CSV/JSON，multipart/form-data：file）</summary>
    [HttpPost("batch-import/tags/{deviceId}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> ImportTags(string deviceId, CancellationToken ct)
    {
        var importLock = ImportLocks.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));
        await importLock.WaitAsync(ct);
        try { return await ImportTagsCoreAsync(deviceId, ct); }
        finally { importLock.Release(); }
    }

    private async Task<IActionResult> ImportTagsCoreAsync(string deviceId, CancellationToken ct)
    {
        var result = await ProxyForwardAsync(
            HttpMethod.Post,
            $"{BatchImportPath}/tags/{Uri.EscapeDataString(deviceId)}",
            ct);
        if (result is not ContentResult { StatusCode: 200, Content: not null } response)
            return result;
        if (!_devices.ReadAll().Any(device => device.Id == deviceId && device.Protocol == "HuazhongRobot"))
            return result;
        using var document = JsonDocument.Parse(response.Content);
        var body = document.RootElement;
        if (body.TryGetProperty("addressMap", out var map) && map.ValueKind == JsonValueKind.String
            && body.TryGetProperty("successCount", out var count) && count.GetInt32() > 0)
        {
            _devices.Update(rows =>
            {
                var index = rows.FindIndex(device => device.Id == deviceId && device.Protocol == "HuazhongRobot");
                if (index < 0) return false;
                rows[index] = rows[index] with { ExtendedProperties = new(rows[index].ExtendedProperties)
                    { ["AddressMap"] = map.GetString()! } };
                return true;
            });
        }
        return result;
    }
}
