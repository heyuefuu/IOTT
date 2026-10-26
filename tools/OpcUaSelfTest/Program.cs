// 用官方 UA-.NETStandard 客户端验证 DeviceSimGateway 的 OPC UA 服务端：连匿名端点、读 Sim.X 等值。
// 用法： dotnet run --project tools/OpcUaSelfTest -- [endpoint]
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

var url = args.Length > 0 ? args[0] : "opc.tcp://localhost:4840/DeviceSimGateway";
var pki = Path.Combine(Path.GetTempPath(), "OpcUaSelfTest", "pki");

try
{
    var config = new ApplicationConfiguration
    {
        ApplicationName = "OpcUaSelfTest",
        ApplicationUri = "urn:OpcUaSelfTest",
        ApplicationType = ApplicationType.Client,
        ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000, MinSubscriptionLifetime = 10000 },
        SecurityConfiguration = new SecurityConfiguration
        {
            ApplicationCertificate = new CertificateIdentifier { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "own"), SubjectName = "CN=OpcUaSelfTest" },
            TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "trusted") },
            TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "issuer") },
            RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "rejected") },
            AutoAcceptUntrustedCertificates = true,
        },
        TransportConfigurations = new TransportConfigurationCollection(),
        TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
        TraceConfiguration = new TraceConfiguration(),
    };
    await config.Validate(ApplicationType.Client);
    config.CertificateValidator.CertificateValidation += (_, e) => e.Accept = true;

    var app = new ApplicationInstance { ApplicationName = "OpcUaSelfTest", ApplicationType = ApplicationType.Client, ApplicationConfiguration = config };
    await app.CheckApplicationInstanceCertificate(false, 2048);

    var ep = CoreClientUtils.SelectEndpoint(config, url, false);
    var cfg = EndpointConfiguration.Create(config);
    var endpoint = new ConfiguredEndpoint(null, ep, cfg);
    var session = await Session.Create(config, endpoint, false, "OpcUaSelfTest", 60000, new UserIdentity(new AnonymousIdentityToken()), null);
    Console.WriteLine($"[OK] 已连接 {url}");

    // 解析命名空间索引
    var nsArray = session.ReadValue(VariableIds.Server_NamespaceArray).Value as string[];
    int ns = Array.IndexOf(nsArray!, "urn:DeviceSimGateway:Sim");
    if (ns < 0) ns = 1;
    foreach (var name in new[] { "X", "Y", "Z", "Spindle", "Feed", "PartCount" })
    {
        var dv = session.ReadValue(new NodeId($"Sim.{name}", (ushort)ns));
        Console.WriteLine($"Sim.{name} = {dv.Value}  (status {dv.StatusCode})");
    }
    session.Close();
    Console.WriteLine("[PASS] OPC UA 服务端互通正常");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine("[FAIL] " + ex.GetType().Name + ": " + ex.Message);
    if (ex is ServiceResultException sre) Console.WriteLine("  status=" + sre.StatusCode);
    return 1;
}
