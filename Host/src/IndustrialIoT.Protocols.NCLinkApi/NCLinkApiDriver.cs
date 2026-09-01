namespace IndustrialIoT.Protocols.NCLinkApi;

using System.Text.Json.Nodes;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

/// <summary>
/// 华中数控 NC-Link 协议驱动（通过 NC-Link API Server HTTP REST）。
/// 严格对照《NC-Link应用开发指导手册》V1.0.1 第 5/8/9 章实现。
///
/// 架构: 上层 → NCLinkApiDriver → HTTP → NC-Link API Server (jar) → MQTT → 数控机床
///
/// 必备配置（ExtendedProperties）：
///   ApiBaseUrl  — API Server 基地址，如 http://127.0.0.1:19001
///   DeviceId    — 设备 SN 码（手册 4.4 示例 1AFFFD1E7F36CAD）
/// 可选：
///   ApiTimeoutMs       — HTTP 超时，默认 30000
///   DefaultRequestTimeoutMs — 单次请求 timeout 字段（手册表 5-1），默认不传
/// </summary>
[ProtocolDriver(ProtocolType.NCLinkApi,
    "华中数控", "HNC", "HNC-8", "HNC-808", "HNC-818", "HNC-848", "HNC-848Di", "HNC-9", "HNC-10",
    "NCLink", "NC-Link", "*")]
public sealed partial class NCLinkApiDriver : IProtocolDriver, IAddressSpaceBrowser
{
    private readonly ILogger<NCLinkApiDriver> _logger;
    private NCLinkApiClient? _client;
    private DeviceConnectionConfig? _config;
    private string _deviceId = "";
    private int? _defaultRequestTimeoutMs;
    private ConnectionState _state = ConnectionState.Disconnected;

    public NCLinkApiDriver(ILogger<NCLinkApiDriver> logger)
    {
        _logger = logger;
    }

    public ProtocolType Protocol => ProtocolType.NCLinkApi;
    public ConnectionState State => _state;

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Read | DriverCapabilities.Write |
        DriverCapabilities.Browse | DriverCapabilities.BatchRead |
        DriverCapabilities.FileTransfer;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    // ── 连接 ─────────────────────────────────────────────────────────────

    public Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        if (_state == ConnectionState.Connected)
            return Task.FromResult(new ConnectionResult { Success = true });

        _config = config;
        TransitionState(ConnectionState.Connecting);

        try
        {
            var ext = config.ExtendedProperties;
            var apiBaseUrl = ext.GetValueOrDefault("ApiBaseUrl")
                ?? BuildBaseUrlFromHostPort(config.Host, config.Port);
            _deviceId = ext.GetValueOrDefault("DeviceId")
                ?? throw new NCLinkApiException("DeviceId is required in ExtendedProperties (机床 SN 码)");

            var timeout = int.TryParse(ext.GetValueOrDefault("ApiTimeoutMs"), out var t) && t > 0
                ? TimeSpan.FromMilliseconds(t)
                : config.ReadTimeout > TimeSpan.Zero ? config.ReadTimeout : TimeSpan.FromSeconds(30);

            _defaultRequestTimeoutMs = int.TryParse(ext.GetValueOrDefault("DefaultRequestTimeoutMs"), out var dt) && dt > 0
                ? dt
                : null;

            _client = new NCLinkApiClient(new Uri(apiBaseUrl), timeout, _logger);

            TransitionState(ConnectionState.Connected);
            _logger.LogInformation("NC-Link API connected: {Base} device={Device}", apiBaseUrl, _deviceId);
            return Task.FromResult(new ConnectionResult { Success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NC-Link API connect failed");
            TransitionState(ConnectionState.Faulted, ex.Message);
            return Task.FromResult(new ConnectionResult { Success = false, ErrorMessage = ex.Message });
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_client is not null)
            await _client.DisposeAsync().ConfigureAwait(false);
        _client = null;
        TransitionState(ConnectionState.Disconnected);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_state != ConnectionState.Connected || _client is null) return false;
        try
        {
            // 用手册 8.1.1 的 /MACHINE/STATUS 做轻量探活
            var resp = await _client.GetValueAsync(_deviceId, NCLinkApiPaths.MachineStatus,
                timeoutMs: _defaultRequestTimeoutMs, ct: ct).ConfigureAwait(false);
            return resp.IsSuccess;
        }
        catch
        {
            return false;
        }
    }

    // ── 读取 ─────────────────────────────────────────────────────────────

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        EnsureConnected();
        var parsed = NCLinkApiAddress.Parse(address);
        var item = parsed.ToRequestItem() with { Timeout = parsed.TimeoutMs ?? _defaultRequestTimeoutMs };

        try
        {
            var resp = await _client!.InvokeAsync(_deviceId, new NCLinkApiRequest
            {
                Operation = NCLinkApiOperations.GetValue,
                Items = [item],
            }, ct).ConfigureAwait(false);

            if (!resp.IsSuccess)
                return BadTagValue(address, dataType,
                    $"{resp.StatusCode.Describe()} (code={resp.Code} status={resp.Status})");

            var raw = ExtractFirstItemValue(resp.Value);
            return BuildTagValue(address, dataType, raw);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NC-Link API read failed: {Address}", address);
            return BadTagValue(address, dataType, ex.Message);
        }
    }

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(
        IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        EnsureConnected();
        if (requests.Count == 0) return [];

        var items = requests
            .Select(r => NCLinkApiAddress.Parse(r.Address).ToRequestItem() with
            {
                Timeout = _defaultRequestTimeoutMs,
            })
            .ToList();

        try
        {
            var resp = await _client!.BatchGetValueAsync(_deviceId, items, ct).ConfigureAwait(false);
            if (!resp.IsSuccess)
                return requests.Select(r => BadTagValue(r.Address, r.DataType,
                    $"{resp.StatusCode.Describe()} (code={resp.Code})")).ToList();

            // 手册 8.2：value 外层数组对应 items 顺序，内层数组对应 index
            var outer = resp.Value as JsonArray;
            var result = new List<TagValue>(requests.Count);
            for (var i = 0; i < requests.Count; i++)
            {
                JsonNode? inner = (outer is not null && i < outer.Count) ? outer[i] : null;
                var first = ExtractFirstScalar(inner);
                result.Add(BuildTagValue(requests[i].Address, requests[i].DataType, first));
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NC-Link API batch read failed ({Count} tags)", requests.Count);
            return requests.Select(r => BadTagValue(r.Address, r.DataType, ex.Message)).ToList();
        }
    }

    // ── 写入 ─────────────────────────────────────────────────────────────

    public async Task<WriteResult> WriteTagAsync(
        string address, DataType dataType, object value, CancellationToken ct = default)
    {
        EnsureConnected();
        var parsed = NCLinkApiAddress.Parse(address);
        var jsonValue = ConvertToJsonNode(value, dataType);

        try
        {
            var resp = await _client!.InvokeAsync(_deviceId, new NCLinkApiRequest
            {
                Operation = NCLinkApiOperations.SetValue,
                Items = [new NCLinkApiRequestItem
                {
                    Path = parsed.Path,
                    Index = parsed.Index,
                    Offset = parsed.Offset,
                    Key = parsed.Key,
                    Value = jsonValue,
                    Timeout = parsed.TimeoutMs ?? _defaultRequestTimeoutMs,
                }],
            }, ct).ConfigureAwait(false);

            if (!resp.IsSuccess)
                return new() { Success = false,
                    ErrorMessage = $"{resp.StatusCode.Describe()} (code={resp.Code})" };

            // 手册：set 响应 value 内层第一个元素是 true/false
            var ok = ExtractFirstItemValue(resp.Value)?.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => true,  // 状态码 0 视为成功
            };
            return new() { Success = ok,
                ErrorMessage = ok ? null : "Device returned false for set_value" };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NC-Link API write failed: {Address}", address);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    // ── IAsyncDisposable ────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Private
    // ═════════════════════════════════════════════════════════════════════

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected || _client is null)
            throw new InvalidOperationException("NC-Link API driver is not connected");
    }

    private void TransitionState(ConnectionState newState, string? reason = null)
    {
        var old = _state;
        if (old == newState) return;
        _state = newState;
        StateChanged?.Invoke(this, new() { OldState = old, NewState = newState, Reason = reason });
    }

    private static string BuildBaseUrlFromHostPort(string host, int port)
    {
        var p = port > 0 ? port : 19001;
        return host.Contains("://", StringComparison.Ordinal)
            ? host.TrimEnd('/')
            : $"http://{host}:{p}";
    }

    // 手册响应 value 是嵌套数组 [[v1, v2, ...], ...]，外层 = items，内层 = index
    private static JsonNode? ExtractFirstItemValue(JsonNode? respValue)
    {
        if (respValue is not JsonArray outer || outer.Count == 0) return null;
        var first = outer[0];
        return ExtractFirstScalar(first);
    }

    private static JsonNode? ExtractFirstScalar(JsonNode? node)
    {
        if (node is JsonArray inner)
            return inner.Count == 0 ? null : inner[0];
        return node;
    }

    private static TagValue BuildTagValue(string address, DataType dataType, JsonNode? raw)
    {
        object value;
        var quality = TagQuality.Good;

        try
        {
            value = ConvertJsonToObject(raw, dataType);
            if (raw is null) quality = TagQuality.Uncertain;
        }
        catch
        {
            return BadTagValue(address, dataType, "Type conversion failed");
        }

        return new TagValue
        {
            Address = address,
            DataType = dataType,
            Value = value,
            Quality = quality,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    private static object ConvertJsonToObject(JsonNode? node, DataType dataType)
    {
        if (node is null) return GetDefaultValue(dataType);
        return dataType switch
        {
            DataType.Bool => node.GetValue<bool>(),
            DataType.Int16 => node.GetValue<short>(),
            DataType.Int32 => node.GetValue<int>(),
            DataType.Int64 => node.GetValue<long>(),
            DataType.UInt16 => node.GetValue<ushort>(),
            DataType.UInt32 => node.GetValue<uint>(),
            DataType.Float => node.GetValue<float>(),
            DataType.Double => node.GetValue<double>(),
            DataType.String => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node.ToJsonString(),
            DataType.ByteArray => Convert.FromBase64String(node.GetValue<string>()),
            _ => node.ToJsonString(),
        };
    }

    private static JsonNode ConvertToJsonNode(object value, DataType dataType) => dataType switch
    {
        DataType.Bool => JsonValue.Create(Convert.ToBoolean(value)),
        DataType.Int16 => JsonValue.Create(Convert.ToInt16(value)),
        DataType.Int32 => JsonValue.Create(Convert.ToInt32(value)),
        DataType.Int64 => JsonValue.Create(Convert.ToInt64(value)),
        DataType.UInt16 => JsonValue.Create(Convert.ToUInt16(value)),
        DataType.UInt32 => JsonValue.Create(Convert.ToUInt32(value)),
        DataType.Float => JsonValue.Create(Convert.ToSingle(value)),
        DataType.Double => JsonValue.Create(Convert.ToDouble(value)),
        DataType.String => JsonValue.Create(Convert.ToString(value) ?? ""),
        DataType.ByteArray => JsonValue.Create(Convert.ToBase64String((byte[])value)),
        _ => JsonValue.Create(value.ToString() ?? ""),
    };

    private static object GetDefaultValue(DataType dt) => dt switch
    {
        DataType.Bool => false,
        DataType.String => string.Empty,
        DataType.ByteArray => Array.Empty<byte>(),
        _ => 0,
    };

    private static TagValue BadTagValue(string address, DataType dt, string error) => new()
    {
        Address = address, DataType = dt,
        Value = GetDefaultValue(dt),
        Quality = TagQuality.Bad,
        Timestamp = DateTimeOffset.UtcNow,
        ErrorMessage = error,
    };
}
