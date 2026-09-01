using MachineConnectionApi.Data;
using MachineConnectionApi.Entities;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

namespace MachineConnectionApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public partial class DatacollectionController : ControllerBase
{
    private readonly MCConfigurationDbContext _db;
    private readonly ILogger<DatacollectionController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IInfluxTelemetryWriter _influxTelemetryWriter;
    private readonly IMqttTelemetryPublisher _mqttTelemetryPublisher;
    private readonly INCLinkApiClient _ncLinkApiClient;
    private readonly INCLinkDeviceResolver _deviceResolver;

    public DatacollectionController(
        MCConfigurationDbContext db,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IInfluxTelemetryWriter influxTelemetryWriter,
        IMqttTelemetryPublisher mqttTelemetryPublisher,
        INCLinkApiClient ncLinkApiClient,
        INCLinkDeviceResolver deviceResolver,
        ILogger<DatacollectionController> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _influxTelemetryWriter = influxTelemetryWriter;
        _mqttTelemetryPublisher = mqttTelemetryPublisher;
        _ncLinkApiClient = ncLinkApiClient;
        _deviceResolver = deviceResolver;
        _logger = logger;
    }

    /// <summary>当前设备已配置的 path 列表，用于点位配置弹窗默认勾选。</summary>
    [HttpGet("paths")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetPaths(
        [FromQuery] string deviceId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return BadRequest(new { error = "deviceId 必填" });

        var id = deviceId.Trim();
        var paths = await _db.Datacollections
            .AsNoTracking()
            .Where(x => x.DeviceId == id)
            .Select(x => x.Path)
            .ToListAsync(ct);
        return paths;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DatacollectionItemDto>>> List(
        [FromQuery] string deviceId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return BadRequest(new { error = "deviceId 必填" });

        var id = deviceId.Trim();
        var list = await _db.Datacollections
            .AsNoTracking()
            .Where(x => x.DeviceId == id)
            .OrderBy(x => x.Path)
            .Select(x => new DatacollectionItemDto(
                x.Id,
                x.Name,
                x.Path,
                x.Datatype,
                x.Datetime,
                x.CollectionFrequency))
            .ToListAsync(ct);
        return list;
    }

    /// <summary>
    /// 一次性执行当前设备的数据采集（第三方只需传 deviceId）。
    /// </summary>
    /// <param name="deviceId">设备唯一标识（必填）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>
    /// 请求示例：
    ///
    ///     GET /api/datacollection/collect?deviceId=demo-device-001
    ///
    /// 成功响应示例（200）：
    ///
    ///     {
    ///       "deviceId": "demo-device-001",
    ///       "collectedAt": "2026-04-10T10:20:30.123+08:00",
    ///       "totalCount": 2,
    ///       "successCount": 2,
    ///       "failCount": 0,
    ///       "items": [
    ///         {
    ///           "name": "主轴转速",
    ///           "path": "Channel/Spindle/Speed",
    ///           "dataType": "Float",
    ///           "value": 1250.5,
    ///           "quality": "Good",
    ///           "timestamp": "2026-04-10T02:20:30.000Z",
    ///           "status": "成功",
    ///           "errorMessage": null,
    ///           "collectionFrequency": 500
    ///         }
    ///       ]
    ///     }
    ///
    /// 失败响应示例（400）：
    ///
    ///     { "error": "deviceId 必填" }
    /// </remarks>
    [ProducesResponseType(typeof(DatacollectionCollectResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status502BadGateway)]
    [HttpGet("collect")]
    public async Task<IActionResult> Collect(
        [FromQuery] string deviceId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return BadRequest(new { error = "deviceId 必填" });

        var id = deviceId.Trim();
        var points = await _db.Datacollections
            .AsNoTracking()
            .Where(x => x.DeviceId == id)
            .OrderBy(x => x.Path)
            .Select(x => new DatacollectionPointDto(
                x.Name,
                x.Path,
                x.Datatype,
                x.CollectionFrequency,
                x.Protocol))
            .ToListAsync(ct);

        if (points.Count == 0)
            return BadRequest(new { error = "该设备暂无已保存采集点位，请先配置 datacollection" });

        var distinctProtocols = points
            .Select(p => NormalizeProtocol(p.Protocol))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctProtocols.Count > 1)
            return BadRequest(new
            {
                error = "同一设备的所有采集点位必须使用同一协议，请检查 datacollection.protocol",
                protocols = distinctProtocols,
            });

        var protocol = distinctProtocols[0];
        List<DatacollectionCollectItemDto> items;
        try
        {
            items = protocol.Equals("NCLinkApi", StringComparison.OrdinalIgnoreCase)
                ? await ReadFromNCLinkApiAsync(await ResolveNCLinkApiDeviceIdAsync(id, ct), points, ct)
                : await ReadFromIndustrialIoTAsync(id, points, ct);
        }
        catch (UpstreamReadException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message, detail = ex.Detail });
        }

        var now = DateTime.Now;
        var successCount = items.Count(x => x.Status == "成功");
        var result = new DatacollectionCollectResultDto(
            id,
            now,
            items.Count,
            successCount,
            items.Count - successCount,
            items);

        var batchTime = DateTimeOffset.UtcNow;
        var telemetryPoints = items
            .Select(i => new InfluxTelemetryPoint(
                i.Name,
                i.Path,
                i.DataType,
                i.Value,
                i.Quality,
                i.Timestamp,
                i.Status,
                i.ErrorMessage))
            .ToList();

        try
        {
            await _influxTelemetryWriter.WriteBatchAsync(id, batchTime, telemetryPoints, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "采集结果写入 InfluxDB 失败（仍返回 HTTP 200 与采集结果）");
        }

        try
        {
            await _mqttTelemetryPublisher.PublishCollectionBatchAsync(id, batchTime, telemetryPoints, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "采集结果 MQTT 发布失败（仍返回 HTTP 200 与采集结果）");
        }

        return Ok(result);
    }

    /// <summary>按勾选项批量插入或更新（按 device_id + path 唯一）。保存时间写入 datetime 字段（日期部分）。</summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync(
        [FromBody] DatacollectionSyncRequest request,
        CancellationToken ct)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.DeviceId))
            return BadRequest(new { error = "deviceId 必填" });
        if ((request.Items == null || request.Items.Count == 0) &&
            (request.VisiblePaths == null || request.VisiblePaths.Count == 0))
            return BadRequest(new { error = "items 和 visiblePaths 不能同时为空" });

        var deviceId = request.DeviceId.Trim();
        var today = DateTime.Now.Date;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var visiblePathSet = new HashSet<string>(
                (request.VisiblePaths ?? Array.Empty<string>())
                    .Select(p => (p ?? "").Trim())
                    .Where(p => p.Length > 0));

            var selectedPathSet = new HashSet<string>(
                (request.Items ?? Array.Empty<DatacollectionSyncItem>())
                    .Select(i => (i.Path ?? "").Trim())
                    .Where(p => p.Length > 0));

            // 与界面勾选保持一致：当前可见列表中取消勾选的点位从库中删除
            if (visiblePathSet.Count > 0)
            {
                var toDelete = await _db.Datacollections
                    .Where(x => x.DeviceId == deviceId &&
                                visiblePathSet.Contains(x.Path) &&
                                !selectedPathSet.Contains(x.Path))
                    .ToListAsync(ct);
                if (toDelete.Count > 0)
                {
                    _db.Datacollections.RemoveRange(toDelete);
                }
            }

            foreach (var item in request.Items ?? Array.Empty<DatacollectionSyncItem>())
            {
                var path = (item.Path ?? "").Trim();
                var name = (item.Name ?? "").Trim();
                var datatype = (item.Datatype ?? "").Trim();
                var freq = item.CollectionFrequency ?? 500;
                if (freq <= 0) freq = 500;
                if (path.Length == 0 || name.Length == 0)
                    continue;

                var proto = NormalizeProtocol(item.Protocol);

                var existing = await _db.Datacollections
                    .FirstOrDefaultAsync(x => x.DeviceId == deviceId && x.Path == path, ct);

                if (existing != null)
                {
                    existing.Name = name.Length > 50 ? name[..50] : name;
                    existing.Datatype = datatype.Length > 50 ? datatype[..50] : datatype;
                    existing.Datetime = today;
                    existing.CollectionFrequency = freq;
                    existing.Protocol = proto;
                }
                else
                {
                    _db.Datacollections.Add(new Datacollection
                    {
                        DeviceId = deviceId,
                        Path = path.Length > 500 ? path[..500] : path,
                        Name = name.Length > 50 ? name[..50] : name,
                        Datatype = string.IsNullOrEmpty(datatype)
                            ? "Variable"
                            : (datatype.Length > 50 ? datatype[..50] : datatype),
                        Datetime = today,
                        CollectionFrequency = freq,
                        Protocol = proto,
                    });
                }
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Ok(new { saved = request.Items?.Count ?? 0 });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "同步 datacollection 失败");
            return StatusCode(500, new
            {
                error = "保存到数据库失败，请确认已创建 MachineCollection 库、表 datacollection。",
                detail = ex.Message,
            });
        }
    }

    private static string NormalizeDataType(string? dt)
    {
        var v = (dt ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(v)) return "Float";
        var lower = v.ToLowerInvariant();
        if (lower is "bool" or "boolean" or "bit" or "sbool") return "Bool";
        if (lower is "int8" or "sbyte" or "sint") return "Int16";
        if (lower == "int16") return "Int16";
        if (lower is "int32" or "int" or "dint") return "Int32";
        if (lower is "int64" or "long" or "lint") return "Int64";
        if (lower is "uint8" or "byte" or "usint") return "UInt16";
        if (lower is "uint16" or "word" or "uint") return "UInt16";
        if (lower is "uint32" or "dword" or "udint") return "UInt32";
        if (lower is "float" or "real" or "sreal") return "Float";
        if (lower is "double" or "lreal") return "Double";
        if (lower is "string" or "char" or "wchar" or "text" or "wstring") return "String";
        if (lower == "bytearray") return "ByteArray";
        return v;
    }

    private static string MapDbDatatypeToReadType(string dbDatatype)
    {
        var t = (dbDatatype ?? string.Empty).Trim();
        if (t is "Variable" or "Folder") return "Float";
        return NormalizeDataType(t);
    }
}

public record DatacollectionItemDto(
    int Id,
    string Name,
    string Path,
    string Datatype,
    DateTime? Datetime,
    int CollectionFrequency);

public record DatacollectionSyncItem(
    string Name,
    string Path,
    string Datatype,
    int? CollectionFrequency,
    string? Protocol = null);

public record DatacollectionSyncRequest(
    string DeviceId,
    IReadOnlyList<DatacollectionSyncItem> Items,
    IReadOnlyList<string>? VisiblePaths);

public record DatacollectionPointDto(
    string Name,
    string Path,
    string Datatype,
    int CollectionFrequency,
    string Protocol);

public record DatacollectionCollectItemDto(
    string Name,
    string Path,
    string DataType,
    object? Value,
    string Quality,
    string Timestamp,
    string Status,
    string? ErrorMessage,
    int CollectionFrequency);

public record DatacollectionCollectResultDto(
    string DeviceId,
    DateTime CollectedAt,
    int TotalCount,
    int SuccessCount,
    int FailCount,
    IReadOnlyList<DatacollectionCollectItemDto> Items);

public class ReadTagProxyItem
{
    public string Address { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public object? Value { get; set; }
    public string Quality { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}

public class ReadTagsProxyResponse
{
    public string DeviceId { get; set; } = string.Empty;
    public List<ReadTagProxyItem> Tags { get; set; } = new();
}

public record ApiErrorResponse(
    string Error,
    string? Detail = null);

internal sealed class UpstreamReadException : Exception
{
    public int StatusCode { get; }
    public string? Detail { get; }

    public UpstreamReadException(int statusCode, string message, string? detail)
        : base(message)
    {
        StatusCode = statusCode;
        Detail = detail;
    }
}
