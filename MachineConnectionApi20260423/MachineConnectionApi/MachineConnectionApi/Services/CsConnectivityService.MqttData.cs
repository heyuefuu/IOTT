using System.Text;
using MachineConnectionApi.Models;
using MQTTnet;
using MQTTnet.Client;

namespace MachineConnectionApi.Services;

public sealed partial class CsConnectivityService
{
    private static async Task<string> ReadMqttAsync(CsGateway gateway, CancellationToken ct)
    {
        using var client = new MqttFactory().CreateMqttClient();
        var topic = "iott/verification/" + Guid.NewGuid().ToString("N");
        var payload = Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += args =>
        {
            if (args.ApplicationMessage.Topic == topic && Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment) == payload)
                received.TrySetResult();
            return Task.CompletedTask;
        };
        var options = new MqttClientOptionsBuilder().WithTcpServer(gateway.Ip, gateway.Port)
            .WithClientId("iott-" + Guid.NewGuid().ToString("N"));
        if (!string.IsNullOrWhiteSpace(gateway.Username)) options.WithCredentials(gateway.Username, gateway.Password);
        await client.ConnectAsync(options.Build(), ct);
        var result = await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(topic).Build(), ct);
        if (result.Items.Any(item => (int)item.ResultCode >= 128)) throw new IOException("MQTT subscription rejected");
        await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).Build(), ct);
        await received.Task.WaitAsync(ct);
        return "MQTT CONNECT + SUBSCRIBE + PUBLISH: payload received and verified";
    }
}
