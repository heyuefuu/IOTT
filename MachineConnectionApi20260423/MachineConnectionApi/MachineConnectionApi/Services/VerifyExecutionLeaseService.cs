namespace MachineConnectionApi.Services;

using System.Text.Json;
using MachineConnectionApi.Models;

/// <summary>
/// One process-wide reservation table for task, background, scheduler and direct HTTP execution.
/// A lease is acquired exactly once at the entry boundary, never by the measurement adapter.
/// </summary>
public sealed class VerifyExecutionLeaseService
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, HashSet<string>?> Active = new();
    private readonly IDeviceStore? _devices;
    private readonly IHttpClientFactory? _http;
    private readonly IConfiguration? _configuration;
    private readonly int _limit;

    // Legacy callers without a registry still participate, conservatively exclusively. In the host,
    // DI supplies both registries; a local upstreamSynced flag is NOT evidence of current agreement.
    public static VerifyExecutionLeaseService Exclusive { get; } = new();

    public VerifyExecutionLeaseService(IConfiguration? configuration = null, IDeviceStore? devices = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        _devices = devices;
        _http = httpClientFactory;
        _configuration = configuration;
        _limit = Math.Clamp(configuration?.GetValue<int?>("VerifyTasks:MaxConcurrentTasks")
            ?? VerifyTaskRunner.MaxConcurrentTasks, 1, VerifyTaskRunner.MaxConcurrentTasks);
    }

    public async Task<IDisposable> AcquireAsync(string? deviceId, VerifyRunOptions? options, CancellationToken ct,
        bool allowIdOnlyReservation = false)
    {
        ct.ThrowIfCancellationRequested();
        VerifyTaskOptionsValidation.Validate(options, _devices);
        HashSet<string>? keys = null;
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var ids = new[] { deviceId.Trim() }.Concat(options?.ConcurrentDeviceIds ?? []).Distinct(StringComparer.Ordinal).ToArray();
            if (_devices is not null) keys = ResolveTargetKeys(ids, _devices.ReadAll(), "网关 App_Data/devices.json");
            else if (allowIdOnlyReservation) keys = ids.Select(id => "device:" + id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (_http is not null)
            {
                // Read both stores independently; reserve their UNION if they differ. Never synchronize
                // or write devices here. The measurement adapter separately freezes/validates its target.
                using var response = await _http.CreateClient("IndustrialIoT").GetAsync(
                    _configuration?["IndustrialIoT:DevicesPath"] ?? "api/Devices", ct);
                response.EnsureSuccessStatusCode();
                var upstream = await response.Content.ReadFromJsonAsync<List<MachineDeviceDto>>(
                    new JsonSerializerOptions(JsonSerializerDefaults.Web), ct)
                    ?? throw new ArgumentException("IoT 后端设备列表为空，无法确认实际执行目标。");
                var upstreamKeys = ResolveTargetKeys(ids, upstream, "IoT 后端设备库");
                if (keys is null) keys = upstreamKeys; else keys.UnionWith(upstreamKeys);
            }
        }
        ct.ThrowIfCancellationRequested();
        lock (Gate)
        {
            if (Active.Count > 0 && (keys is null || Active.Values.Any(value => value is null)))
                throw new VerifyTaskCapacityException("目标尚未明确的验证需要独占执行，请等待当前任务完成后重试");
            if (keys is not null && Active.Values.Any(value => value is not null && value.Overlaps(keys)))
                throw new VerifyTaskCapacityException("该机床或并发测试目标已有验证任务正在执行，请等待完成后重试");
            if (Active.Count >= _limit)
                throw new VerifyTaskCapacityException($"同时执行的验证任务已达上限（{_limit} 个），请稍后重试");
            var id = Guid.NewGuid();
            Active.Add(id, keys);
            return new Lease(id);
        }
    }

    public static HashSet<string> ResolveTargetKeys(IEnumerable<string> ids, IReadOnlyList<MachineDeviceDto> devices, string source)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MachineDeviceDto Lookup(string id)
        {
            var matches = devices.Where(device => string.Equals(device.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || matches[0].Id != id)
                throw new ArgumentException($"{source}：设备 {id} 不存在或标识不唯一，请分别核对两套设备库。");
            return matches[0];
        }
        void AddEndpoint(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host) || port is < 0 or > 65535)
                throw new ArgumentException($"{source}：设备通信端点无效，无法预约实际目标。");
            keys.Add($"endpoint:{host.Trim().TrimEnd('.').ToLowerInvariant()}:{port}");
        }
        MachineDeviceDto? LinkedDevice(MachineDeviceDto device)
        {
            // Host's inline Transfer branch wins; an unused dangling link must not shadow it.
            if (device.Transfer is not null) return null;
            var link = device.ExtendedProperties?.GetValueOrDefault("transferDeviceId");
            if (string.IsNullOrWhiteSpace(link)) return null;
            if (link != link.Trim()) throw new ArgumentException($"{source}：transferDeviceId 格式无效。");
            return Lookup(link);
        }
        void ValidateGraph(MachineDeviceDto device, HashSet<string> path)
        {
            if (!path.Add(device.Id)) throw new ArgumentException($"{source}：transferDeviceId 存在循环引用。");
            if (path.Count > 100) throw new ArgumentException($"{source}：transferDeviceId 引用层级过深。");
            if (LinkedDevice(device) is { } linked) ValidateGraph(linked, path);
            path.Remove(device.Id);
        }
        void AddPrimary(MachineDeviceDto device)
        {
            keys.Add("device:" + device.Id);
            AddEndpoint(device.Host, device.Port);
        }
        foreach (var id in ids)
        {
            var device = Lookup(id);
            ValidateGraph(device, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            AddPrimary(device);
            if (device.Transfer is { } transfer) AddEndpoint(transfer.Host, transfer.Port);
            // Host connects the linked device's PRIMARY endpoint; it does not recursively switch
            // to that linked row's Transfer. Graph validation above is deliberately separate.
            else if (LinkedDevice(device) is { } linked) AddPrimary(linked);
        }
        return keys;
    }

    private sealed class Lease(Guid id) : IDisposable
    {
        public void Dispose() { lock (Gate) Active.Remove(id); }
    }
}
