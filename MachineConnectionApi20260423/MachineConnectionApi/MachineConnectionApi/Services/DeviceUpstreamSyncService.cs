namespace MachineConnectionApi.Services;

using System.Text;
using System.Net.Http.Json;
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
        using var registryOperation = await DeviceRegistryGate.EnterAsync(_store, ct);
        var rows = _store.ReadAll();
        var seeded = 0;
        if (rows.Count == 0)
        {
            var restoreReport = await RestoreEmptyRegistryAsync(ct);
            // 上游确认为空（全新部署）时才用随代码分发的种子设备；上游有数据时始终以上游为准
            if (restoreReport.Total > 0 || (seeded = SeedEmptyRegistry()) == 0)
                return restoreReport;
            rows = _store.ReadAll();
        }
        var created = 0;
        var updated = 0;
        var skipped = 0;
        var errors = new List<UpstreamSyncError>();
        var results = new Dictionary<string, UpstreamSyncResult>();

        for (var i = 0; i < rows.Count; i++)
        {
            var result = rows[i].RestoredFromUpstream
                ? await ProbeRestoredAsync(rows[i], ct)
                : await UpsertAsync(rows[i], ct);
            results[rows[i].Id] = result;
            if (result.Success)
            {
                if (result.Action == "created") created++;
                else if (result.Action == "preserved") skipped++;
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
            Seeded = seeded,
            Total = rows.Count,
            Created = created,
            Updated = updated,
            Skipped = skipped,
            Failed = errors.Count,
            Errors = errors,
        };
    }

    private async Task<UpstreamSyncResult> ProbeRestoredAsync(MachineDeviceDto device, CancellationToken ct)
    {
        try
        {
            using var response = await _httpClientFactory.CreateClient("IndustrialIoT")
                .GetAsync($"{DevicesPath}/{Uri.EscapeDataString(device.Id)}", ct);
            return response.IsSuccessStatusCode
                ? UpstreamSyncResult.Ok("preserved")
                : UpstreamSyncResult.Fail("probe", await DescribeAsync(response, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "恢复设备 {DeviceId} 上游探测失败", device.Id);
            return UpstreamSyncResult.Fail("probe", ex.Message);
        }
    }

    private async Task<UpstreamSyncReport> RestoreEmptyRegistryAsync(CancellationToken ct)
    {
        using var response = await _httpClientFactory.CreateClient("IndustrialIoT").GetAsync(DevicesPath, ct);
        response.EnsureSuccessStatusCode();
        var devices = DeviceSnapshot.Parse(await response.Content.ReadAsStringAsync(ct), "上游设备列表");
        var restored = devices.Select(device => device with
        {
            Password = null,
            Transfer = device.Transfer is null ? null : device.Transfer with { Password = null },
            RestoredFromUpstream = true,
            UpstreamSynced = true,
            UpstreamError = null,
        }).ToList();
        ct.ThrowIfCancellationRequested();
        // Check emptiness again under the store lock: a user may have added a device during the GET.
        return _store.Update(current =>
        {
            if (current.Count != 0)
                return new UpstreamSyncReport { Total = current.Count, Skipped = current.Count };
            current.AddRange(restored);
            _logger.LogInformation("从上游恢复 {Count} 台设备到网关，保留原设备 ID，未回写上游", restored.Count);
            return new UpstreamSyncReport { Total = restored.Count, Restored = restored.Count };
        });
    }

    /// <summary>
    /// 源码随附的 SeedData/devices.json（格式同导出文件）写入空注册表，返回写入台数。
    /// 文件缺失返回 0；文件损坏记错误日志后返回 0，不阻断正常对账。
    /// </summary>
    private int SeedEmptyRegistry()
    {
        var path = _configuration["IndustrialIoT:DeviceSeedPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "SeedData", "devices.json");
        if (!File.Exists(path)) return 0;
        List<MachineDeviceDto> seeds;
        try
        {
            seeds = DeviceSnapshot.Parse(File.ReadAllText(path), $"种子设备文件 {path} ");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "种子设备文件 {Path} 读取失败，未初始化设备", path);
            return 0;
        }
        return _store.Update(current =>
        {
            if (current.Count != 0 || seeds.Count == 0) return 0;
            current.AddRange(seeds.Select(DeviceSnapshot.ForTransfer));
            _logger.LogInformation("网关与上游均无设备，已从种子文件 {Path} 初始化 {Count} 台设备", path, seeds.Count);
            return seeds.Count;
        });
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
            ["password"] = device.Password,
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
        else if (!includeId)
        {
            payload["clearTransfer"] = true;
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
