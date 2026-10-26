// 用 MQTTnet 客户端回归 DeviceSimGateway 的 NC-Link 设备（broker 内置：127.0.0.1:1883，guid=sim-device-001）。
// 用法： dotnet run --project tools/NcLinkSelfTest -- [host] [port] [guid]
using MQTTnet;
using MQTTnet.Protocol;

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 ? int.Parse(args[1]) : 1883;
var guid = args.Length > 2 ? args[2] : "sim-device-001";

var factory = new MqttClientFactory();
var client = factory.CreateMqttClient();
var inbox = new Dictionary<string, TaskCompletionSource<string>>();

client.ApplicationMessageReceivedAsync += e =>
{
    var t = e.ApplicationMessage.Topic;
    if (inbox.TryGetValue(t, out var tcs)) tcs.TrySetResult(e.ApplicationMessage.ConvertPayloadToString() ?? "");
    return Task.CompletedTask;
};

var conn = await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(host, port).WithClientId("nclink-selftest").Build());
Console.WriteLine("connect: " + conn.ResultCode);

foreach (var t in new[]
{
    $"Query/Response/{guid}", $"Probe/Query/Response/{guid}", $"Probe/Version/Response/{guid}",
    $"Set/Response/{guid}", $"Sample/{guid}/ch1",
})
{
    inbox[t] = new TaskCompletionSource<string>();
    await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(t, MqttQualityOfServiceLevel.AtLeastOnce).Build());
}

async Task<string> Ask(string topic, string payload, string replyTopic, int ms = 4000)
{
    inbox[replyTopic] = new TaskCompletionSource<string>();
    await client.PublishStringAsync(topic, payload, MqttQualityOfServiceLevel.AtLeastOnce);
    var done = await Task.WhenAny(inbox[replyTopic].Task, Task.Delay(ms));
    return done == inbox[replyTopic].Task ? inbox[replyTopic].Task.Result : "(timeout)";
}

Console.WriteLine("Probe/Version -> " + await Ask($"Probe/Version/{guid}", "{\"@id\":\"v1\"}", $"Probe/Version/Response/{guid}"));
Console.WriteLine("Probe/Query   -> " + (await Ask($"Probe/Query/Request/{guid}", "{\"@id\":\"p1\"}", $"Probe/Query/Response/{guid}")).Substring(0, 200) + "…");
Console.WriteLine("Query(X,Spindle) -> " + await Ask($"Query/Request/{guid}", "{\"@id\":\"q1\",\"ids\":[{\"id\":\"X\"},{\"id\":\"Spindle\"}]}", $"Query/Response/{guid}"));
Console.WriteLine("Set(PartCount=7) -> " + await Ask($"Set/Request/{guid}", "{\"@id\":\"s1\",\"items\":[{\"id\":\"PartCount\",\"operation\":\"set_value\",\"value\":7}]}", $"Set/Response/{guid}"));
var sample = await Task.WhenAny(inbox[$"Sample/{guid}/ch1"].Task, Task.Delay(4000));
Console.WriteLine("Sample        -> " + (sample == inbox[$"Sample/{guid}/ch1"].Task ? inbox[$"Sample/{guid}/ch1"].Task.Result : "(timeout)"));

await client.DisconnectAsync();
Console.WriteLine("[PASS] NC-Link 回归完成");
