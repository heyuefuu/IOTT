namespace IndustrialIoT.Protocols.NCLink;

using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.NCLink.BrandAdapters;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

/// <summary>
/// NC-Link 协议驱动 — 通过 MQTT pub/sub 对接 NC-Link 标准协议。
/// 架构: CNC设备 → NC-Link Adapter → MQTT Broker → 本驱动
/// </summary>
[ProtocolDriver(ProtocolType.NCLink,
    "Mazak", "马扎克", "Haas", "哈斯", "Brother", "兄弟",
    "Makino", "牧野", "华中数控")]
public sealed partial class NCLinkDriver : IProtocolDriver, IAddressSpaceBrowser, IProgramFileBrowser, INCProgramTransfer
{
    private readonly ILogger<NCLinkDriver> _logger;

    private IMqttClient? _mqttClient;
    private string _deviceGuid = "";
    private DeviceConnectionConfig? _config;
    private IBrandAdapter _brandAdapter = new DefaultBrandAdapter();
    private ConnectionState _state = ConnectionState.Disconnected;
    private TimeSpan _readTimeout = TimeSpan.FromSeconds(5);

    // 请求-响应匹配: @id → TCS
    private readonly ConcurrentDictionary<string, TaskCompletionSource<NCLinkResponse>> _pending = new();

    // 缓存的设备模型（Probe 结果）
    private NCLinkDeviceModel? _deviceModel;

    // Sample 数据回调
    private Action<string, IReadOnlyList<QueryResultValue>>? _onSampleData;

    // ── IProtocolDriver 属性 ─────────────────────────────────────────

    public ProtocolType Protocol => ProtocolType.NCLink;
    public ConnectionState State => _state;

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Read | DriverCapabilities.Write |
        DriverCapabilities.Browse | DriverCapabilities.BatchRead |
        DriverCapabilities.FileTransfer;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public NCLinkDriver(ILogger<NCLinkDriver> logger)
    {
        _logger = logger;
    }

    // ── 连接 ─────────────────────────────────────────────────────────

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        if (_state == ConnectionState.Connected)
            return new() { Success = true };

        _config = config;
        TransitionState(ConnectionState.Connecting);

        try
        {
            // 解析配置
            var ext = config.ExtendedProperties;
            _deviceGuid = ext.GetValueOrDefault("DeviceGuid")
                ?? throw new NCLinkProtocolException("DeviceGuid is required in ExtendedProperties");

            var brand = ext.GetValueOrDefault("Brand");
            _brandAdapter = BrandAdapterFactory.Create(brand);

            var brokerHost = ext.GetValueOrDefault("MqttBrokerHost") ?? config.Host;
            var brokerPort = int.TryParse(ext.GetValueOrDefault("MqttBrokerPort"), out var p) ? p : config.Port;
            if (brokerPort <= 0) brokerPort = 1883;

            var username = ext.GetValueOrDefault("MqttUsername") ?? config.Username;
            var password = ext.GetValueOrDefault("MqttPassword") ?? config.Password;
            var useTls = ext.GetValueOrDefault("UseTls")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

            _readTimeout = config.ReadTimeout;

            // 创建 MQTT 客户端
            _mqttClient = new MqttClientFactory().CreateMqttClient();
            _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
            _mqttClient.DisconnectedAsync += OnDisconnectedAsync;

            // 构建连接选项
            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithTcpServer(brokerHost, brokerPort)
                .WithClientId($"IIoT-NCLink-{Environment.MachineName}-{Guid.NewGuid():N}".Substring(0, 23))
                .WithTimeout(config.ConnectTimeout);

            if (!string.IsNullOrEmpty(username))
                optionsBuilder.WithCredentials(username, password);

            if (useTls)
            {
                var tlsOptions = new MqttClientTlsOptions { UseTls = true };
                var caPath = ext.GetValueOrDefault("CaCertPath");
                if (!string.IsNullOrEmpty(caPath) && File.Exists(caPath))
                {
                    var caCert = new System.Security.Cryptography.X509Certificates.X509Certificate2(caPath);
                    tlsOptions.TrustChain = [caCert];
                }
                optionsBuilder.WithTlsOptions(tlsOptions);
            }

            // 连接
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(config.ConnectTimeout);

            var connectResult = await _mqttClient.ConnectAsync(optionsBuilder.Build(), connectCts.Token);
            if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
                throw new NCLinkProtocolException($"MQTT connect failed: {connectResult.ResultCode}");

            // 订阅响应 Topic
            var responseTopics = NCLinkProtocol.GetResponseTopics(_deviceGuid);
            var subOptions = new MqttClientSubscribeOptionsBuilder();
            foreach (var topic in responseTopics)
                subOptions.WithTopicFilter(topic, MqttQualityOfServiceLevel.AtLeastOnce);

            await _mqttClient.SubscribeAsync(subOptions.Build(), ct);

            // 订阅 Sample Topic（如配置了）
            var sampleChannels = ext.GetValueOrDefault("SampleChannels");
            if (!string.IsNullOrEmpty(sampleChannels))
            {
                var subBuilder = new MqttClientSubscribeOptionsBuilder();
                foreach (var ch in sampleChannels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    subBuilder.WithTopicFilter(NCLinkProtocol.SampleTopic(_deviceGuid, ch), MqttQualityOfServiceLevel.AtMostOnce);
                await _mqttClient.SubscribeAsync(subBuilder.Build(), ct);
            }

            // Probe — 获取设备模型
            await LoadDeviceModelAsync(ct);

            TransitionState(ConnectionState.Connected);
            _logger.LogInformation("NC-Link connected via MQTT to {Host}:{Port}, device={Guid}, brand={Brand}",
                brokerHost, brokerPort, _deviceGuid, _brandAdapter.Brand);

            return new() { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NC-Link connection failed: {Guid}", _deviceGuid);
            await CleanupAsync();
            TransitionState(ConnectionState.Faulted, ex.Message);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await CleanupAsync();
        TransitionState(ConnectionState.Disconnected);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_state != ConnectionState.Connected || _mqttClient is not { IsConnected: true })
            return false;

        try
        {
            // 用 Probe/Version 做轻量 ping
            var msgId = NCLinkProtocol.NextMessageId();
            var payload = NCLinkProtocol.BuildProbeVersionRequest(msgId, _deviceModel?.Version);
            var response = await PublishAndWaitAsync(
                NCLinkProtocol.ProbeVersionTopic(_deviceGuid), payload, msgId, ct);
            return response.Code.Equals("OK", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ── 读取 ─────────────────────────────────────────────────────────

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        EnsureConnected();
        var dataItemId = ResolveDataItemId(address);
        var msgId = NCLinkProtocol.NextMessageId();
        var payload = NCLinkProtocol.BuildQueryRequest(msgId, [dataItemId]);

        try
        {
            var response = await PublishAndWaitAsync(
                NCLinkProtocol.QueryRequestTopic(_deviceGuid), payload, msgId, ct);
            NCLinkProtocol.ThrowIfError(response);

            var values = NCLinkProtocol.ParseQueryResponse(response.Raw);
            if (values.Count == 0)
                return BadTagValue(address, dataType, "Empty response");

            return ToTagValue(values[0], address, dataType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NC-Link read failed: {Address}", address);
            return BadTagValue(address, dataType, ex.Message);
        }
    }

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(
        IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        EnsureConnected();
        var ids = requests.Select(r => ResolveDataItemId(r.Address)).ToList();
        var distinctIds = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var msgId = NCLinkProtocol.NextMessageId();
        var payload = NCLinkProtocol.BuildQueryRequest(msgId, distinctIds);

        try
        {
            var response = await PublishAndWaitAsync(
                NCLinkProtocol.QueryRequestTopic(_deviceGuid), payload, msgId, ct);
            NCLinkProtocol.ThrowIfError(response);

            var values = NCLinkProtocol.ParseQueryResponse(response.Raw);
            return MapBatchReadResults(requests, ids, values);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NC-Link batch read failed ({Count} tags)", requests.Count);
            return requests.Select(r => BadTagValue(r.Address, r.DataType, ex.Message)).ToList();
        }
    }

    // ── 写入 ─────────────────────────────────────────────────────────

    public async Task<WriteResult> WriteTagAsync(
        string address, DataType dataType, object value, CancellationToken ct = default)
    {
        EnsureConnected();
        var dataItemId = ResolveDataItemId(address);
        var msgId = NCLinkProtocol.NextMessageId();
        var items = new[] { new SetItem(dataItemId, "set_value", value) };
        var payload = NCLinkProtocol.BuildSetRequest(msgId, items);

        try
        {
            var response = await PublishAndWaitAsync(
                NCLinkProtocol.SetRequestTopic(_deviceGuid), payload, msgId, ct);
            NCLinkProtocol.ThrowIfError(response);
            _logger.LogDebug("NC-Link write OK: {Address} = {Value}", address, value);
            return new() { Success = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NC-Link write failed: {Address}", address);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    // ── IAddressSpaceBrowser ─────────────────────────────────────────

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(
        string? parentPath = null, CancellationToken ct = default)
    {
        // 优先从缓存的设备模型构建地址树
        if (_deviceModel is { DataItems.Count: > 0 })
        {
            var nodes = BuildAddressTreeFromModel(_deviceModel, parentPath);
            if (nodes.Count > 0)
                return Task.FromResult<IReadOnlyList<AddressNode>>(nodes);
        }

        // Fallback: 静态标准地址树
        return Task.FromResult(GetStaticAddressTree(parentPath));
    }

    public Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var nodes = BrowseAsync(null, ct).GetAwaiter().GetResult();
        var sb = new StringBuilder();
        if (format == ExportFormat.CSV)
        {
            sb.AppendLine("Path,DisplayName,DataType,Readable,Writable");
            foreach (var node in FlattenNodes(nodes))
                sb.AppendLine($"{node.Path},{node.DisplayName},{node.DataType},{node.IsReadable},{node.IsWritable}");
        }
        else
        {
            sb.Append(JsonSerializer.Serialize(FlattenNodes(nodes).ToList(), new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));
        }
        Stream stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        return Task.FromResult(stream);
    }

    // ── Sample 订阅 ──────────────────────────────────────────────────

    /// <summary>注册 Sample 数据回调。channelId → values。</summary>
    public void OnSampleData(Action<string, IReadOnlyList<QueryResultValue>> callback)
    {
        _onSampleData = callback;
    }

    /// <summary>获取缓存的设备模型。</summary>
    public NCLinkDeviceModel? DeviceModel => _deviceModel;

    internal string MapDevicePathToStandardPath(string devicePath)
    {
        // 优先动态：用 Probe 模型里的 name 反查
        var match = _deviceModel?.DataItems.FirstOrDefault(di =>
            di.Id.Equals(devicePath, StringComparison.OrdinalIgnoreCase));
        if (match is not null && !string.IsNullOrEmpty(match.Name))
            return match.Name;
        // 静态 fallback：品牌适配器
        return _brandAdapter.MapPathReverse(devicePath);
    }

    /// <summary>
    /// 把上层入参（标准路径 / DataItem ID / DataItem name）解析为机床真实 DataItem ID。
    /// 优先级：Probe 模型 ID 直通 → Probe 模型 name 反查 → 品牌适配器静态映射 → 原样透传。
    /// </summary>
    private string ResolveDataItemId(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return address;

        if (_deviceModel is { DataItems.Count: > 0 } model)
        {
            // 直通：address 本身就是 Probe 报出的 DataItem ID
            if (model.DataItems.Any(di => di.Id.Equals(address, StringComparison.OrdinalIgnoreCase)))
                return address;
            // 反查：address 是 Probe 报出的 name
            var byName = model.DataItems.FirstOrDefault(di =>
                di.Name.Equals(address, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName.Id;
        }

        // 兜底：品牌适配器（DefaultBrandAdapter 透传，HNCBrandAdapter 静态字典）
        return _brandAdapter.MapPath(address);
    }

    // ── IAsyncDisposable ─────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Private
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>MQTT 消息到达回调。</summary>
    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payloadBytes = e.ApplicationMessage.Payload.ToArray();
        var payload = Encoding.UTF8.GetString(payloadBytes);

        try
        {
            // Sample 数据
            if (topic.StartsWith($"Sample/{_deviceGuid}/", StringComparison.Ordinal))
            {
                var channelId = topic.Substring($"Sample/{_deviceGuid}/".Length);
                var sampleValues = NCLinkProtocol.ParseSampleData(payload);
                _onSampleData?.Invoke(channelId, sampleValues);
                return Task.CompletedTask;
            }

            // 请求-响应匹配
            var response = NCLinkProtocol.ParseResponse(payload);
            if (_pending.TryRemove(response.MessageId, out var tcs))
            {
                tcs.TrySetResult(response);
            }
            else
            {
                _logger.LogDebug("NC-Link unmatched response: @id={Id} on {Topic}", response.MessageId, topic);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NC-Link message parse error on {Topic}", topic);
        }

        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (_state == ConnectionState.Connected)
        {
            _logger.LogWarning("NC-Link MQTT disconnected: {Reason}", e.Reason);
            TransitionState(ConnectionState.Faulted, e.Reason.ToString());
        }
        return Task.CompletedTask;
    }

    /// <summary>发布消息并等待匹配响应。</summary>
    private async Task<NCLinkResponse> PublishAndWaitAsync(
        string topic, string payload, string messageId, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<NCLinkResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[messageId] = tcs;

        try
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient!.PublishAsync(msg, ct);

            // 超时等待
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_readTimeout);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));
            if (completedTask == tcs.Task)
                return await tcs.Task;

            throw new TimeoutException($"NC-Link response timeout for {messageId} on {topic}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"NC-Link response timeout for {messageId} on {topic}");
        }
        finally
        {
            _pending.TryRemove(messageId, out _);
        }
    }

    /// <summary>连接后加载设备模型。</summary>
    private async Task LoadDeviceModelAsync(CancellationToken ct)
    {
        try
        {
            var msgId = NCLinkProtocol.NextMessageId();
            var payload = NCLinkProtocol.BuildProbeQueryRequest(msgId);
            var response = await PublishAndWaitAsync(
                NCLinkProtocol.ProbeQueryRequestTopic(_deviceGuid), payload, msgId, ct);

            _deviceModel = NCLinkProtocol.ParseProbeResponse(response.Raw);
            if (_deviceModel is not null)
                _logger.LogInformation("NC-Link device model loaded: {ItemCount} data items, {ChannelCount} sample channels",
                    _deviceModel.DataItems.Count, _deviceModel.SampleChannels.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NC-Link Probe failed, will use static address tree as fallback");
        }
    }

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected || _mqttClient is not { IsConnected: true })
            throw new InvalidOperationException("NC-Link driver is not connected");
    }

    private void TransitionState(ConnectionState newState, string? reason = null)
    {
        var old = _state;
        if (old == newState) return;
        _state = newState;
        StateChanged?.Invoke(this, new() { OldState = old, NewState = newState, Reason = reason });
    }

    private async Task CleanupAsync()
    {
        // 取消所有 pending 请求
        foreach (var kv in _pending)
            kv.Value.TrySetCanceled();
        _pending.Clear();

        if (_mqttClient is { IsConnected: true })
        {
            try { await _mqttClient.DisconnectAsync(); } catch { /* ignore */ }
        }
        _mqttClient?.Dispose();
        _mqttClient = null;
        _deviceModel = null;
    }

    // ── 数据转换 ─────────────────────────────────────────────────────

    private static TagValue ToTagValue(QueryResultValue qv, string address, DataType dataType)
    {
        // 关键修复：机床对未知 DataItem ID 经常回 {value:null, quality:null} 而不是错误码。
        // 不要静默吞成 Uncertain+0，让上层看到真实问题。
        if (qv.Value is null && string.IsNullOrEmpty(qv.Quality))
            return BadTagValue(address, dataType,
                $"DataItem '{qv.Id}' not present on device or returned null payload (check Probe model for real IDs)");

        var quality = qv.Quality?.ToLowerInvariant() switch
        {
            "good" => TagQuality.Good,
            "bad" => TagQuality.Bad,
            _ => TagQuality.Uncertain,
        };

        object value;
        try { value = ConvertValue(qv.Value, dataType); }
        catch { return BadTagValue(address, dataType, "Data type conversion failed"); }

        return new()
        {
            Address = address,
            DataType = dataType,
            Value = value,
            Quality = quality,
            Timestamp = qv.Timestamp.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(qv.Timestamp.Value)
                : DateTimeOffset.UtcNow,
        };
    }

    private static IReadOnlyList<TagValue> MapBatchReadResults(
        IReadOnlyList<TagReadRequest> requests,
        IReadOnlyList<string> ids,
        IReadOnlyList<QueryResultValue> values)
    {
        var valueMap = new Dictionary<string, QueryResultValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value.Id)) continue;
            valueMap.TryAdd(value.Id, value);
        }

        var result = new List<TagValue>(requests.Count);
        for (var i = 0; i < requests.Count; i++)
        {
            var req = requests[i];
            var id = ids[i];
            if (valueMap.TryGetValue(id, out var value))
                result.Add(ToTagValue(value, req.Address, req.DataType));
            else
                result.Add(BadTagValue(req.Address, req.DataType, "Missing in response"));
        }
        return result;
    }

    private static object ConvertValue(JsonNode? node, DataType dataType)
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
            DataType.String => node.GetValue<string>(),
            DataType.ByteArray => Convert.FromBase64String(node.GetValue<string>()),
            _ => node.GetValue<double>(),
        };
    }

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

    // ── 地址树构建 ───────────────────────────────────────────────────

    private static List<AddressNode> BuildAddressTreeFromModel(NCLinkDeviceModel model, string? parentPath)
    {
        // 按 ComponentPath 分组
        var groups = model.DataItems
            .GroupBy(di => di.ComponentPath ?? "Device")
            .OrderBy(g => g.Key);

        var root = new List<AddressNode>();
        foreach (var group in groups)
        {
            var children = group.Select(di => new AddressNode
            {
                Path = di.Id,
                DisplayName = string.IsNullOrEmpty(di.Name) ? di.Id : di.Name,
                NodeType = AddressNodeType.Variable,
                DataType = MapNCLinkType(di.Type),
                IsReadable = true,
                IsWritable = di.Settable,
            }).ToList();

            root.Add(new AddressNode
            {
                Path = group.Key,
                DisplayName = group.Key,
                NodeType = AddressNodeType.Folder,
                IsReadable = false,
                IsWritable = false,
                Children = children,
            });
        }

        if (parentPath is null) return root;

        // 子树筛选：递归找到 path == parentPath 的节点，返回其 children；
        // 若 parentPath 命中变量节点本身，则把它包成单元素列表返回。
        var normalized = parentPath.TrimEnd('/');
        var hit = FindNodeByPath(root, normalized);
        if (hit is null) return [];
        if (hit.NodeType == AddressNodeType.Folder)
            return hit.Children?.ToList() ?? [];
        return [hit];
    }

    private static AddressNode? FindNodeByPath(IEnumerable<AddressNode> nodes, string path)
    {
        foreach (var n in nodes)
        {
            if (n.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) return n;
            if (n.Children is { Count: > 0 })
            {
                var sub = FindNodeByPath(n.Children, path);
                if (sub is not null) return sub;
            }
        }
        return null;
    }

    private static DataType? MapNCLinkType(string ncType) => ncType.ToUpperInvariant() switch
    {
        "STATUS" or "STATE" or "MODE" or "STRING" => DataType.String,
        "SPEED" or "LOAD" or "TEMPERATURE" or "PRESSURE" or "FLOAT" => DataType.Float,
        "POSITION" or "COORDINATE" or "DOUBLE" => DataType.Double,
        "COUNT" or "NUMBER" or "INT" or "INT32" => DataType.Int32,
        "FLAG" or "BOOL" or "BOOLEAN" => DataType.Bool,
        _ => DataType.String,
    };

    // ── 静态地址树（Fallback） ───────────────────────────────────────

    private static IReadOnlyList<AddressNode> GetStaticAddressTree(string? parentPath)
    {
        var allNodes = BuildFullTree();
        if (parentPath is null) return allNodes;
        var normalized = parentPath.TrimEnd('/');
        var hit = FindNodeByPath(allNodes, normalized);
        if (hit is null) return [];
        if (hit.NodeType == AddressNodeType.Folder)
            return hit.Children?.ToList() ?? [];
        return [hit];
    }

    private static IReadOnlyList<AddressNode> BuildFullTree() =>
    [
        new()
        {
            Path = "/CNC", DisplayName = "CNC", NodeType = AddressNodeType.Folder,
            IsReadable = false, IsWritable = false,
            Children =
            [
                new()
                {
                    Path = "/CNC/Spindle", DisplayName = "主轴 Spindle", NodeType = AddressNodeType.Folder,
                    IsReadable = false, IsWritable = false,
                    Children =
                    [
                        new() { Path = "/CNC/Spindle/Speed", DisplayName = "主轴转速", NodeType = AddressNodeType.Variable, DataType = DataType.Float, IsReadable = true, IsWritable = false },
                        new() { Path = "/CNC/Spindle/Load", DisplayName = "主轴负载", NodeType = AddressNodeType.Variable, DataType = DataType.Float, IsReadable = true, IsWritable = false },
                    ],
                },
                new()
                {
                    Path = "/CNC/Axis", DisplayName = "轴 Axis", NodeType = AddressNodeType.Folder,
                    IsReadable = false, IsWritable = false,
                    Children =
                    [
                        MakeAxisNode("X"), MakeAxisNode("Y"), MakeAxisNode("Z"),
                        MakeAxisNode("A"), MakeAxisNode("B"), MakeAxisNode("C"),
                    ],
                },
                new()
                {
                    Path = "/CNC/Program", DisplayName = "程序 Program", NodeType = AddressNodeType.Folder,
                    IsReadable = false, IsWritable = false,
                    Children =
                    [
                        new() { Path = "/CNC/Program/Number", DisplayName = "当前程序号", NodeType = AddressNodeType.Variable, DataType = DataType.String, IsReadable = true, IsWritable = true },
                        new() { Path = "/CNC/Program/Status", DisplayName = "运行状态", NodeType = AddressNodeType.Variable, DataType = DataType.String, IsReadable = true, IsWritable = false },
                    ],
                },
                new()
                {
                    Path = "/CNC/Alarm", DisplayName = "报警 Alarm", NodeType = AddressNodeType.Folder,
                    IsReadable = false, IsWritable = false,
                    Children =
                    [
                        new() { Path = "/CNC/Alarm/Active", DisplayName = "报警激活", NodeType = AddressNodeType.Variable, DataType = DataType.Bool, IsReadable = true, IsWritable = false },
                        new() { Path = "/CNC/Alarm/Code", DisplayName = "报警代码", NodeType = AddressNodeType.Variable, DataType = DataType.Int32, IsReadable = true, IsWritable = false },
                        new() { Path = "/CNC/Alarm/Message", DisplayName = "报警信息", NodeType = AddressNodeType.Variable, DataType = DataType.String, IsReadable = true, IsWritable = false },
                    ],
                },
                new() { Path = "/CNC/Mode", DisplayName = "操作模式", NodeType = AddressNodeType.Variable, DataType = DataType.String, IsReadable = true, IsWritable = false },
                new() { Path = "/CNC/PartCount", DisplayName = "加工计数", NodeType = AddressNodeType.Variable, DataType = DataType.Int32, IsReadable = true, IsWritable = true },
            ],
        },
    ];

    private static AddressNode MakeAxisNode(string name) => new()
    {
        Path = $"/CNC/Axis/{name}", DisplayName = $"{name}轴", NodeType = AddressNodeType.Folder,
        IsReadable = false, IsWritable = false,
        Children =
        [
            new() { Path = $"/CNC/Axis/{name}/Position", DisplayName = $"{name}轴位置", NodeType = AddressNodeType.Variable, DataType = DataType.Double, IsReadable = true, IsWritable = false },
            new() { Path = $"/CNC/Axis/{name}/FeedRate", DisplayName = $"{name}轴进给速度", NodeType = AddressNodeType.Variable, DataType = DataType.Float, IsReadable = true, IsWritable = true },
        ],
    };

    private static IEnumerable<AddressNode> FlattenNodes(IReadOnlyList<AddressNode> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.NodeType == AddressNodeType.Variable)
                yield return n;
            if (n.Children is not null)
                foreach (var child in FlattenNodes(n.Children))
                    yield return child;
        }
    }
}
