namespace DeviceSimGateway.Services;

using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceSimGateway.Core;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

/// <summary>NC-Link 模拟设备：内置 MQTT Broker + 设备端应答 + 周期采样。
/// topic/字段严格对齐 Host 端 NCLinkProtocol（Query/Set/Probe/Version/Register + Sample/{guid}/{ch}）。</summary>
public sealed class NcLinkService : IDeviceService
{
    public string Key => "ncLink";
    public string Name => "NC-Link (MQTT)";
    public string Description => "NC-Link 服务端（内置 MQTT Broker + 设备应答 + 采样）";
    public int DefaultPort => 1883;
    public bool Implemented => true;
    public ServiceState State { get; private set; } = ServiceState.Stopped;
    public string Message { get; private set; } = "";
    public int Port { get; private set; }

    private const string Guid = "sim-device-001";
    private const string ChannelId = "ch1";

    private MqttServer? _server;
    private IMqttClient? _client;
    private SimDataStore? _store;
    private Timer? _timer;

    private static readonly (string Id, string Name, string Type, bool Settable, string Unit)[] Items =
    [
        ("X", "X 位置", "FLOAT", false, "mm"),
        ("Y", "Y 位置", "FLOAT", false, "mm"),
        ("Z", "Z 位置", "FLOAT", false, "mm"),
        ("Spindle", "主轴转速", "FLOAT", false, "rpm"),
        ("Feed", "进给速度", "FLOAT", false, "mm/min"),
        ("PartCount", "加工计数", "INT32", true, ""),
    ];

    public async Task StartAsync(int port, SimDataStore data, GeneratorConfig gen, CancellationToken ct)
    {
        _store = data; Port = port;

        // 1) 内置 MQTT Broker
        var serverOptions = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(port)
            .Build();
        _server = new MqttServerFactory().CreateMqttServer(serverOptions);
        await _server.StartAsync();

        // 2) 设备端 MQTT 客户端（连本机 broker）
        var factory = new MqttClientFactory();
        _client = factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        var clientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", port)
            .WithClientId($"sim-{Guid}")
            .Build();
        await _client.ConnectAsync(clientOptions, ct);

        // 订阅请求类 topic
        var sub = new MqttClientSubscribeOptionsBuilder();
        foreach (var t in new[]
        {
            $"Query/Request/{Guid}", $"Set/Request/{Guid}",
            $"Probe/Query/Request/{Guid}", $"Probe/Set/Request/{Guid}",
            $"Probe/Version/{Guid}", $"Register/Request/{Guid}",
        }) sub.WithTopicFilter(t, MqttQualityOfServiceLevel.AtLeastOnce);
        await _client.SubscribeAsync(sub.Build(), ct);

        // 3) 周期采样
        _timer = new Timer(async _ => await PublishSampleAsync(), null, 1000, 1000);

        State = ServiceState.Running;
        Message = $"Broker 0.0.0.0:{port}；设备 guid={Guid}；采样 Sample/{Guid}/{ChannelId}";
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _timer?.Dispose(); _timer = null;
        if (_client is not null) { try { await _client.DisconnectAsync(); } catch { } _client.Dispose(); _client = null; }
        if (_server is not null) { try { await _server.StopAsync(); } catch { } _server.Dispose(); _server = null; }
        if (State == ServiceState.Running) { State = ServiceState.Stopped; Message = "已停止"; }
    }

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = e.ApplicationMessage.ConvertPayloadToString() ?? "";
        var msgId = ExtractMsgId(payload);
        try
        {
            if (topic == $"Query/Request/{Guid}")
                await PublishAsync($"Query/Response/{Guid}", QueryResponse(msgId, payload));
            else if (topic == $"Set/Request/{Guid}")
                await PublishAsync($"Set/Response/{Guid}", SetResponse(msgId, payload));
            else if (topic == $"Probe/Query/Request/{Guid}")
                await PublishAsync($"Probe/Query/Response/{Guid}", ProbeResponse(msgId));
            else if (topic == $"Probe/Set/Request/{Guid}")
                await PublishAsync($"Probe/Set/Response/{Guid}", $"{{\"@id\":\"{msgId}\",\"code\":\"OK\"}}");
            else if (topic == $"Probe/Version/{Guid}")
                await PublishAsync($"Probe/Version/Response/{Guid}", $"{{\"@id\":\"{msgId}\",\"code\":\"OK\",\"version\":\"1.0\"}}");
            else if (topic == $"Register/Request/{Guid}")
                await PublishAsync($"Register/Response/{Guid}", $"{{\"@id\":\"{msgId}\",\"code\":\"OK\"}}");
        }
        catch { /* 忽略单个请求错误 */ }
    }

    private async Task PublishAsync(string topic, string payload)
    {
        if (_client is not { IsConnected: true }) return;
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _client.PublishAsync(msg);
    }

    private static string ExtractMsgId(string payload)
    {
        try { return JsonNode.Parse(payload)?["@id"]?.GetValue<string>() ?? "msg"; }
        catch { return "msg"; }
    }

    private string QueryResponse(string msgId, string payload)
    {
        var ids = new List<string>();
        try
        {
            var arr = JsonNode.Parse(payload)?["ids"]?.AsArray();
            if (arr is not null) foreach (var x in arr) { var id = x?["id"]?.GetValue<string>(); if (!string.IsNullOrEmpty(id)) ids.Add(id!); }
        }
        catch { }
        if (ids.Count == 0) ids.AddRange(Items.Select(i => i.Id));

        var values = new JsonArray();
        foreach (var id in ids) values.Add(ValueNode(id));
        var obj = new JsonObject { ["@id"] = msgId, ["code"] = "OK", ["values"] = values };
        return obj.ToJsonString();
    }

    private string SetResponse(string msgId, string payload)
    {
        var results = new JsonArray();
        try
        {
            var items = JsonNode.Parse(payload)?["items"]?.AsArray();
            if (items is not null)
            {
                foreach (var it in items)
                {
                    var id = it?["id"]?.GetValue<string>() ?? "";
                    var op = it?["operation"]?.GetValue<string>() ?? "set_value";
                    var value = it?["value"];
                    bool ok = ApplySet(id, op, value);
                    results.Add(new JsonObject { ["id"] = id, ["code"] = ok ? "OK" : "ERR" });
                }
            }
        }
        catch { }
        var obj = new JsonObject { ["@id"] = msgId, ["code"] = "OK", ["results"] = results };
        return obj.ToJsonString();
    }

    private string ProbeResponse(string msgId)
    {
        var dataItems = new JsonArray();
        foreach (var (id, name, type, settable, unit) in Items)
            dataItems.Add(new JsonObject { ["id"] = id, ["name"] = name, ["type"] = type, ["settable"] = settable, ["unit"] = unit });

        var device = new JsonObject
        {
            ["id"] = Guid,
            ["guid"] = Guid,
            ["version"] = "1.0",
            ["dataItems"] = dataItems,
            ["configs"] = new JsonArray(new JsonObject
            {
                ["type"] = "SAMPLE_CHANNEL",
                ["id"] = ChannelId,
                ["sampleInterval"] = 1000,
                ["uploadInterval"] = 1000,
                ["ids"] = new JsonArray(Items.Select(i => (JsonNode)new JsonObject { ["id"] = i.Id }).ToArray()),
            }),
        };
        var obj = new JsonObject { ["@id"] = msgId, ["code"] = "OK", ["devices"] = new JsonArray(device) };
        return obj.ToJsonString();
    }

    private async Task PublishSampleAsync()
    {
        if (_client is not { IsConnected: true }) return;
        var values = new JsonArray();
        foreach (var (id, _, _, _, _) in Items) values.Add(ValueNode(id));
        var obj = new JsonObject { ["@id"] = $"sample-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", ["values"] = values };
        await PublishAsync($"Sample/{Guid}/{ChannelId}", obj.ToJsonString());
    }

    private JsonObject ValueNode(string id)
    {
        var s = _store!;
        object val;
        lock (s.Sync)
        {
            val = id switch
            {
                "X" => Reg(s.Holding, 0) / 100.0,
                "Y" => Reg(s.Holding, 1) / 100.0,
                "Z" => Reg(s.Holding, 2) / 100.0,
                "Spindle" => (double)Reg(s.Input, 0),
                "Feed" => Reg(s.Input, 1) / 10.0,
                "PartCount" => (int)Reg(s.Holding, 3),
                _ => 0.0,
            };
        }
        var node = new JsonObject
        {
            ["id"] = id,
            ["quality"] = "good",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        node["value"] = JsonValue.Create(val);
        return node;
    }

    private bool ApplySet(string id, string operation, JsonNode? value)
    {
        var s = _store!;
        if (id != "PartCount") return false;
        if (!int.TryParse(value?.ToString(), out var v)) return false;
        lock (s.Sync) s.Holding[Math.Min(3, s.Holding.Length - 1)] = (ushort)Math.Clamp(v, 0, 65535);
        return true;
    }

    private static ushort Reg(ushort[] a, int i) => i >= 0 && i < a.Length ? a[i] : (ushort)0;
}
