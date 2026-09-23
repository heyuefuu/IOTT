namespace MachineConnectionApi.Services;

using System.Text.Json;
using MachineConnectionApi.Models;

/// <summary>
/// 完整设备配置快照（导出文件 / SeedData 种子文件 / 上游恢复）的解析、校验与运行态清理。
/// 格式与 App_Data/devices.json 相同，可直接互相拷贝。
/// </summary>
public static class DeviceSnapshot
{
    /// <summary>与 JsonFileStore 写 devices.json 的格式一致（camelCase、缩进），读取时大小写不敏感。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>解析设备数组；任一设备缺少必填字段或 ID 重复即整体拒绝，避免部分写入。</summary>
    public static List<MachineDeviceDto> Parse(string json, string source)
    {
        List<MachineDeviceDto?>? devices;
        try
        {
            devices = JsonSerializer.Deserialize<List<MachineDeviceDto?>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new JsonException($"{source}不是有效的设备 JSON 数组：{ex.Message}", ex);
        }
        if (devices is null)
            throw new JsonException($"{source}为空响应，未恢复本地设备配置。");
        if (!IsValid(devices))
            throw new JsonException($"{source}缺少有效字段或设备 ID 重复，未恢复本地设备配置。");
        return devices!;
    }

    public static bool IsValid(IEnumerable<MachineDeviceDto?> devices)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            if (device is null || string.IsNullOrWhiteSpace(device.Id) || !ids.Add(device.Id)
                || string.IsNullOrWhiteSpace(device.Name) || string.IsNullOrWhiteSpace(device.Type)
                || string.IsNullOrWhiteSpace(device.Protocol) || string.IsNullOrWhiteSpace(device.Host)
                || device.Brand is null || device.Model is null || device.Status is null
                || device.Port < 0 || device.Port > 65535)
                return false;
        }
        return true;
    }

    /// <summary>去掉本机运行态（在线状态、同步结果、恢复标记），保留设备 ID、配置与凭据。</summary>
    public static MachineDeviceDto ForTransfer(MachineDeviceDto device) => device with
    {
        Status = "Offline",
        LastSeenAt = null,
        UpstreamSynced = null,
        UpstreamError = null,
        RestoredFromUpstream = false,
    };
}
