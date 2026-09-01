namespace MachineConnectionApi.Services;

using System.Text;
using System.Text.Json;
using MachineConnectionApi.Models;

public interface IDeviceUpstreamSyncService
{
    Task<UpstreamSyncResult> UpsertAsync(MachineDeviceDto device, CancellationToken ct);
    Task<UpstreamSyncResult> DeleteAsync(string deviceId, CancellationToken ct);
    Task<UpstreamSyncReport> SyncAllAsync(CancellationToken ct);
}

/// <summary>
/// 将网关本地设备注册表（devices.json）镜像到上游 Industrial IoT 设备库。
/// 上游的地址空间 / 读写 / 采集 / 程序传输 / 连接验证均按 deviceId 在上游注册表解析设备，
/// 本地增删改若不镜像到上游，新设备的所有上游功能都会 404。同步为 best-effort：
/// 上游不可用时本地操作照常成功，结果记录在设备的 upstreamSynced / upstreamError 字段。
/// </summary>
public sealed class DeviceUpstreamSyncService : IDeviceUpstreamSyncService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // 网关侧协议名 → 上游 ProtocolType 枚举名（枚举绑定本身大小写不敏感，这里只处理拼写别名）
    private static readonly Dictionary<string, string> ProtocolAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["S7"] = "SiemensS7",
        ["OPCUA"] = "OpcUa",
        ["OPC UA"] = "OpcUa",
        ["OPC-UA"] = "OpcUa",
        ["NC-Link"] = "NCLink",
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDeviceStore _store;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DeviceUpstreamSyncService> _logger;

    public DeviceUpstreamSyncService(
        IHttpClientFactory httpClientFactory,
        IDeviceStore store,
        IConfiguration configuration,
        ILogger<DeviceUpstreamSyncService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _store = store;
        _configuration = configuration;
        _logger = logger;
    }

    private string DevicesPath => _configuration["IndustrialIoT:DevicesPath"] ?? "api/Devices";

    public async Task<UpstreamSyncResult> UpsertAsync(MachineDeviceDto device, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("IndustrialIoT");
            using var probe = await client.GetAsync($"{DevicesPath}/{Uri.EscapeDataString(device.Id)}", ct);
            if (probe.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                using var created = await client.PostAsync(DevicesPath, ToJson(BuildPayload(device, includeId: true)), ct);
                return created.IsSuccessStatusCode
                    ? UpstreamSyncResult.Ok("created")
                    : UpstreamSyncResult.Fail("created", await DescribeAsync(created, ct));
            }
            if (!probe.IsSuccessStatusCode)
                return UpstreamSyncResult.Fail("probe", await DescribeAsync(probe, ct));

            using var updated = await client.PutAsync(
                $"{DevicesPath}/{Uri.EscapeDataString(device.Id)}",
                ToJson(BuildPayload(device, includeId: false)), ct);
            return updated.IsSuccessStatusCode
                ? UpstreamSyncResult.Ok("updated")
                : UpstreamSyncResult.Fail("updated", await DescribeAsync(updated, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "设备 {DeviceId} 同步上游失败", device.Id);
            return UpstreamSyncResult.Fail("upsert", ex.Message);
        }
    }

    public async Task<UpstreamSyncResult> DeleteAsync(string deviceId, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("IndustrialIoT");
            using var response = await client.DeleteAsync($"{DevicesPath}/{Uri.EscapeDataString(deviceId)}", ct);
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound
                ? UpstreamSyncResult.Ok("deleted")
                : UpstreamSyncResult.Fail("deleted", await DescribeAsync(response, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "设备 {DeviceId} 上游删除失败", deviceId);
            return UpstreamSyncResult.Fail("deleted", ex.Message);
        }
    }

    public async Task<UpstreamSyncReport> SyncAllAsync(CancellationToken ct)
    {
        var rows = _store.ReadAll();
        var created = 0;
        var updated = 0;
        var errors = new List<UpstreamSyncError>();
        var results = new Dictionary<string, UpstreamSyncResult>();

        for (var i = 0; i < rows.Count; i++)
        {
            var result = await UpsertAsync(rows[i], ct);
            results[rows[i].Id] = result;
            if (result.Success)
            {
                if (result.Action == "created") created++;
                else updated++;
            }
            else
            {
                errors.Add(new UpstreamSyncError(rows[i].Id, rows[i].Name, result.Error ?? "未知错误"));
            }
        }

        _store.Update(current =>
        {
            for (var index = 0; index < current.Count; index++)
            {
                if (!results.TryGetValue(current[index].Id, out var result)) continue;
                current[index] = current[index] with
                {
                    UpstreamSynced = result.Success,
                    UpstreamError = result.Success ? null : result.Error,
                };
            }
            return 0;
        });
        return new UpstreamSyncReport
        {
            Total = rows.Count,
            Created = created,
            Updated = updated,
            Failed = errors.Count,
            Errors = errors,
        };
    }

    private static Dictionary<string, object?> BuildPayload(MachineDeviceDto device, bool includeId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = device.Name,
            ["type"] = device.Type,
            ["brand"] = string.IsNullOrWhiteSpace(device.Brand) ? "Unknown" : device.Brand,
            ["model"] = string.IsNullOrWhiteSpace(device.Model) ? "Unknown" : device.Model,
            ["protocol"] = NormalizeProtocol(device.Protocol),
            ["host"] = device.Host,
            ["port"] = device.Port,
            ["username"] = device.Username,
            ["connectTimeoutMs"] = device.ConnectTimeoutMs > 0 ? device.ConnectTimeoutMs : 10_000,
            ["readTimeoutMs"] = device.ReadTimeoutMs > 0 ? device.ReadTimeoutMs : 5_000,
            ["extendedProperties"] = device.ExtendedProperties ?? [],
        };
        if (includeId)
            payload["id"] = device.Id;
        if (device.Transfer is { } transfer)
        {
            payload["transfer"] = new Dictionary<string, object?>
            {
                ["protocol"] = NormalizeProtocol(transfer.Protocol),
                ["host"] = transfer.Host,
                ["port"] = transfer.Port,
                ["username"] = transfer.Username,
                ["password"] = transfer.Password,
                ["connectTimeoutMs"] = transfer.ConnectTimeoutMs,
                ["readTimeoutMs"] = transfer.ReadTimeoutMs,
                ["extendedProperties"] = transfer.ExtendedProperties,
            };
        }
        return payload;
    }

    private static string NormalizeProtocol(string? protocol)
    {
        var trimmed = protocol?.Trim() ?? "";
        return ProtocolAliases.TryGetValue(trimmed, out var mapped) ? mapped : trimmed;
    }

    private static StringContent ToJson(Dictionary<string, object?> payload) =>
        new(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

    private static async Task<string> DescribeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Length > 300)
            body = body[..300];
        return $"HTTP {(int)response.StatusCode} {body}".Trim();
    }
}
