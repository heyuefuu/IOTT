using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MachineConnectionApi.Models;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace MachineConnectionApi.Services;

public sealed partial class CsConnectivityService
{
    private bool StartOpcUaServer(CsServerService service)
    {
        if (!string.IsNullOrEmpty(service.Username) || !string.IsNullOrEmpty(service.Password))
            throw new InvalidOperationException("OPCUAServer 当前为 SecurityPolicy=None/Anonymous 测试服务；不接收用户名密码，请清空凭据。");
        var identity = "server-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(service.Id)))[..16];
        var config = OpcUaConfiguration(identity, ApplicationType.Server);
        config.ServerConfiguration = new()
        {
            BaseAddresses = new() { $"opc.tcp://localhost:{service.Port}/cs" },
            SecurityPolicies = new() { new() { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None } },
            UserTokenPolicies = new() { new(UserTokenType.Anonymous) },
            DiagnosticsEnabled = true, MaxSessionCount = service.MaxClients,
            MinSessionTimeout = 1000, MaxSessionTimeout = 60_000,
        };
        var runtime = new ServerRuntime
        {
            Listener = new TcpListener(IPAddress.Loopback, 0), Cts = new(),
            ConnectionSlots = new(service.MaxClients, service.MaxClients),
        };
        var server = new CsOpcUaServer(runtime, service);
        try
        {
            config.ValidateAsync(ApplicationType.Server, default).GetAwaiter().GetResult();
            var application = new ApplicationInstance(config, OpcUaTelemetry);
            if (!application.CheckApplicationInstanceCertificatesAsync(true, 12, default).GetAwaiter().GetResult())
                throw new IOException("OPC UA server application certificate is invalid");
            application.StartAsync(server).GetAwaiter().GetResult();
            runtime.OpcUa = server;
            _serverRuntimes[service.Id] = runtime;
            service.Status = "运行中";
            service.ClientCount = 0;
            _logger.LogInformation("OPC UA 测试服务 {Id} 已启动：SecurityMode=None，Anonymous；命名空间 urn:IndustrialIoT:CsConnectivity 下 IOTT.Cs.Value 可读写", service.Id);
            return true;
        }
        catch (Exception error)
        {
            server.Dispose();
            runtime.Cts.Dispose();
            runtime.ConnectionSlots.Dispose();
            service.Status = "停止";
            throw new InvalidOperationException("OPC UA 服务启动失败：" + error.Message, error);
        }
    }

    private sealed class CsOpcUaServer(ServerRuntime runtime, CsServerService service) : StandardServer
    {
        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration) =>
            new(server, configuration, null, new INodeManager[] { new CsOpcUaNodeManager(server, configuration) });

        protected override void OnServerStarted(IServerInternal server)
        {
            base.OnServerStarted(server);
            server.SessionManager.SessionActivated += (session, _) =>
            {
                runtime.Connections[session.Id.ToString()] = new(session.SessionDiagnostics.ClientDescription.ApplicationUri, Now(), 0);
                service.ClientCount = runtime.Connections.Count;
                service.LastAccess = Now();
            };
            server.SessionManager.SessionClosing += (session, _) =>
            {
                runtime.Connections.TryRemove(session.Id.ToString(), out var ignored);
                service.ClientCount = runtime.Connections.Count;
            };
        }
    }
}
