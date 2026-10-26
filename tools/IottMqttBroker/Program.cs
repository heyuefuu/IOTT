using MQTTnet;
using MQTTnet.Server;

var port = 1883;
if (args.Length > 0 && int.TryParse(args[0], out var parsed) && parsed > 0 && parsed <= 65535)
{
    port = parsed;
}

var options = new MqttServerOptionsBuilder()
    .WithDefaultEndpoint()
    .WithDefaultEndpointPort(port)
    .Build();

var server = new MqttServerFactory().CreateMqttServer(options);
server.ClientConnectedAsync += e =>
{
    Console.WriteLine($"{DateTimeOffset.Now:O} client connected id={e.ClientId}");
    return Task.CompletedTask;
};
server.ClientDisconnectedAsync += e =>
{
    Console.WriteLine($"{DateTimeOffset.Now:O} client disconnected id={e.ClientId}");
    return Task.CompletedTask;
};
await server.StartAsync();
Console.WriteLine($"{DateTimeOffset.Now:O} IOTT MQTT broker listening on 0.0.0.0:{port}");

await Task.Delay(Timeout.Infinite);
