using System.Net;
using System.Net.Sockets;
using MachineConnectionApi.Models;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace MachineConnectionApi.Services;

public sealed partial class CsConnectivityService
{
    private bool StartBroker(CsServerService service)
    {
        var broker = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder()
            .WithDefaultEndpoint().WithDefaultEndpointPort(service.Port).Build());
        var runtime = new ServerRuntime
        {
            Listener = new TcpListener(IPAddress.Loopback, 0),
            Cts = new CancellationTokenSource(),
            ConnectionSlots = new SemaphoreSlim(service.MaxClients, service.MaxClients),
            Broker = broker,
        };
        broker.ValidatingConnectionAsync += args =>
        {
            if (string.IsNullOrWhiteSpace(args.ClientId))
                args.ReasonCode = MqttConnectReasonCode.ClientIdentifierNotValid;
            else if (!string.IsNullOrEmpty(service.Username) &&
                (args.UserName != service.Username || args.Password != service.Password))
                args.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            else if (!runtime.ConnectionSlots.Wait(0))
                args.ReasonCode = MqttConnectReasonCode.ServerBusy;
            else if (!runtime.BrokerReservations.TryAdd(args.ClientId, 0))
            {
                runtime.ConnectionSlots.Release();
                args.ReasonCode = MqttConnectReasonCode.ClientIdentifierNotValid;
            }
            return Task.CompletedTask;
        };
        broker.ClientConnectedAsync += args =>
        {
            runtime.Connections[args.ClientId] = new(args.ClientId, Now(), 0);
            service.ClientCount = runtime.Connections.Count;
            service.LastAccess = Now();
            return Task.CompletedTask;
        };
        broker.ClientDisconnectedAsync += args =>
        {
            runtime.Connections.TryRemove(args.ClientId, out _);
            if (runtime.BrokerReservations.TryRemove(args.ClientId, out _)) runtime.ConnectionSlots.Release();
            service.ClientCount = runtime.Connections.Count;
            return Task.CompletedTask;
        };
        try
        {
            broker.StartAsync().GetAwaiter().GetResult();
            _serverRuntimes[service.Id] = runtime;
            service.Status = "运行中";
            return true;
        }
        catch (Exception error)
        {
            broker.Dispose();
            runtime.Cts.Dispose();
            runtime.ConnectionSlots.Dispose();
            throw new InvalidOperationException("MQTT Broker 启动失败：" + error.Message, error);
        }
    }
}
