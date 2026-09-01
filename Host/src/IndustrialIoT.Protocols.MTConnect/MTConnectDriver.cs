namespace IndustrialIoT.Protocols.MTConnect;

using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

/// <summary>
/// MTConnect 驱动 — 适用于 Mazak（Smooth 系列）、Brother（Speedio）等内置 MTConnect Agent 的数控。
/// 协议：HTTP GET + XML 响应。标准端点 /probe（设备树）、/current（当前快照）、/sample（采样流）。
/// 只读协议；点位地址使用 DataItem.id（MTConnect 标识符）。
/// </summary>
[ProtocolDriver(ProtocolType.MTConnect, "Mazak", "马扎克", "Brother", "兄弟", "MTConnect")]
public sealed class MTConnectDriver : IProtocolDriver, IAddressSpaceBrowser
{
    private const int DefaultPort = 5000; // MTConnect Agent 默认 HTTP 端口

    private readonly ILogger<MTConnectDriver> _logger;
    private readonly HttpClient _http;
    private DeviceConnectionConfig? _config;
    private string? _baseUri;
    private MTConnectWriteAdapterOptions _writeOptions = new();
    private ConnectionState _state = ConnectionState.Disconnected;

    public ProtocolType Protocol => ProtocolType.MTConnect;
    public ConnectionState State => _state;
    public DriverCapabilities Capabilities =>
        DriverCapabilities.Read | DriverCapabilities.Browse | DriverCapabilities.BatchRead |
        (_writeOptions.IsConfigured ? DriverCapabilities.Write : DriverCapabilities.None);

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public MTConnectDriver(ILogger<MTConnectDriver> logger)
    {
        _logger = logger;
        _http = new HttpClient();
    }

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        if (_state == ConnectionState.Connected) return new() { Success = true };
        SetState(ConnectionState.Connecting);

        try
        {
            _config = config;
            _writeOptions = MTConnectWriteAdapterOptions.From(config);
            var port = config.Port > 0 ? config.Port : DefaultPort;
            _baseUri = $"http://{config.Host}:{port}";
            _http.Timeout = config.ConnectTimeout;

            // 连通性验证：GET /probe 应返回 MTConnectDevices XML
            var probeXml = await _http.GetStringAsync($"{_baseUri}/probe", ct);
            if (string.IsNullOrWhiteSpace(probeXml) || !probeXml.Contains("MTConnectDevices"))
                throw new InvalidOperationException("Endpoint did not return MTConnectDevices XML");

            _logger.LogInformation("MTConnect connected to {Base}", _baseUri);
            SetState(ConnectionState.Connected);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MTConnect connection failed");
            SetState(ConnectionState.Faulted, ex.Message);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_state != ConnectionState.Connected || _baseUri is null) return false;
        try
        {
            using var resp = await _http.GetAsync($"{_baseUri}/probe", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        var xml = await FetchXmlAsync("/current", ct);
        var map = MTConnectXmlParser.ParseCurrent(xml);
        return BuildTagValue(address, dataType, map);
    }

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        var xml = await FetchXmlAsync("/current", ct);
        var map = MTConnectXmlParser.ParseCurrent(xml);
        return requests.Select(r => BuildTagValue(r.Address, r.DataType, map)).ToList();
    }

    public Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
    {
        if (!_writeOptions.IsConfigured)
        {
            return Task.FromResult<WriteResult>(new()
            {
                Success = false,
                ErrorMessage = "MTConnect write endpoint is not configured. Set ExtendedProperties['WriteEndpointUrl'] for vendor private writes.",
            });
        }

        return WriteViaAdapterAsync(address, dataType, value, ct);
    }

    public async Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        var xml = await FetchXmlAsync("/probe", ct);
        var tree = MTConnectXmlParser.ParseProbe(xml);
        if (string.IsNullOrEmpty(parentPath)) return tree;
        var node = FindNode(tree, parentPath);
        return node?.Children ?? [];
    }

    public async Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var nodes = await BrowseAsync(null, ct);
        var flat = Flatten(nodes).Where(n => n.NodeType == AddressNodeType.Variable).ToList();
        var ms = new MemoryStream();
        if (format == ExportFormat.JSON)
        {
            await JsonSerializer.SerializeAsync(ms, flat, new JsonSerializerOptions { WriteIndented = true }, ct);
        }
        else
        {
            var sb = new StringBuilder("Path,DisplayName,DataType,Readable,Writable\n");
            foreach (var n in flat)
                sb.Append($"{n.Path},{n.DisplayName},{n.DataType},{n.IsReadable},{n.IsWritable}\n");
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            await ms.WriteAsync(bytes, ct);
        }
        ms.Position = 0;
        return ms;
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private void SetState(ConnectionState next, string? reason = null)
    {
        var old = _state;
        if (old == next) return;
        _state = next;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs
        {
            OldState = old, NewState = next, Reason = reason
        });
    }

    private async Task<string> FetchXmlAsync(string path, CancellationToken ct)
    {
        if (_state != ConnectionState.Connected || _baseUri is null)
            throw new InvalidOperationException("MTConnect driver is not connected");
        return await _http.GetStringAsync($"{_baseUri}{path}", ct);
    }

    private async Task<WriteResult> WriteViaAdapterAsync(
        string address, DataType dataType, object value, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_writeOptions.BearerToken))
            _http.DefaultRequestHeaders.Authorization = new("Bearer", _writeOptions.BearerToken);

        var payload = new MTConnectWriteAdapterRequest(address, dataType.ToString(), value);
        using var response = await _http.PostAsJsonAsync(_writeOptions.EndpointUrl!, payload, ct);
        if (response.IsSuccessStatusCode)
            return new() { Success = true };

        var body = await response.Content.ReadAsStringAsync(ct);
        return new()
        {
            Success = false,
            ErrorMessage = $"MTConnect write adapter returned {(int)response.StatusCode}: {body}",
        };
    }

    private static TagValue BuildTagValue(string address, DataType dataType,
        IReadOnlyDictionary<string, MTConnectXmlParser.CurrentValue> map)
    {
        if (!map.TryGetValue(address, out var cur))
            return new TagValue
            {
                Address = address, DataType = dataType,
                Value = string.Empty, Quality = TagQuality.Bad,
                Timestamp = DateTimeOffset.UtcNow,
                ErrorMessage = $"DataItem '{address}' not present in /current",
            };

        var isUnavailable = string.Equals(cur.Raw, "UNAVAILABLE", StringComparison.OrdinalIgnoreCase);
        return new TagValue
        {
            Address = address, DataType = dataType,
            Value = isUnavailable ? string.Empty : MTConnectXmlParser.CoerceValue(cur.Raw, dataType),
            Quality = isUnavailable ? TagQuality.Uncertain : TagQuality.Good,
            Timestamp = cur.Timestamp,
            ErrorMessage = isUnavailable ? "UNAVAILABLE" : null,
        };
    }

    private static AddressNode? FindNode(IEnumerable<AddressNode> nodes, string path)
    {
        foreach (var n in nodes)
        {
            if (string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)) return n;
            if (n.Children is { Count: > 0 })
            {
                var hit = FindNode(n.Children, path);
                if (hit is not null) return hit;
            }
        }
        return null;
    }

    private static IEnumerable<AddressNode> Flatten(IEnumerable<AddressNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            if (n.Children is { Count: > 0 })
                foreach (var c in Flatten(n.Children)) yield return c;
        }
    }
}
