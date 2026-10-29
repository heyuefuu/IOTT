using Opc.Ua;
using Opc.Ua.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MachineConnectionApi.Services;

public sealed partial class CsConnectivityService
{
    private sealed class CsOpcUaTelemetry() : TelemetryContextBase(NullLoggerFactory.Instance);
    private static readonly ITelemetryContext OpcUaTelemetry = new CsOpcUaTelemetry();
    private static readonly SemaphoreSlim OpcUaConfigurationGate = new(1, 1);
    private static ApplicationConfiguration? _opcUaClientConfiguration;

    private static ApplicationConfiguration OpcUaConfiguration(string identity, ApplicationType type)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "App_Data", "cs-opcua-pki", identity);
        var name = "IOTT-CS-" + identity;
        return new()
        {
            ApplicationName = name, ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:{name}",
            ProductUri = "urn:IndustrialIoT:CsConnectivity", ApplicationType = type,
            SecurityConfiguration = new()
            {
                ApplicationCertificate = new()
                {
                    StoreType = "Directory", StorePath = Path.Combine(root, "own"), SubjectName = "CN=" + name,
                },
                TrustedPeerCertificates = new() { StoreType = "Directory", StorePath = Path.Combine(root, "trusted") },
                TrustedIssuerCertificates = new() { StoreType = "Directory", StorePath = Path.Combine(root, "issuers") },
                RejectedCertificateStore = new() { StoreType = "Directory", StorePath = Path.Combine(root, "rejected") },
                AutoAcceptUntrustedCertificates = false, RejectSHA1SignedCertificates = true, MinimumCertificateKeySize = 2048,
            },
            TransportConfigurations = new(),
            TransportQuotas = new() { OperationTimeout = 3000, MaxMessageSize = 1024 * 1024 },
            ClientConfiguration = type == ApplicationType.Client ? new() { DefaultSessionTimeout = 30_000 } : null,
        };
    }

    private static async Task<ApplicationConfiguration> OpcUaClientConfigurationAsync(CancellationToken ct)
    {
        await OpcUaConfigurationGate.WaitAsync(ct);
        try
        {
            if (_opcUaClientConfiguration is not null) return _opcUaClientConfiguration;
            var config = OpcUaConfiguration("client", ApplicationType.Client);
            await config.ValidateAsync(ApplicationType.Client, ct);
            var application = new ApplicationInstance(config, OpcUaTelemetry);
            if (!await application.CheckApplicationInstanceCertificatesAsync(true, 12, ct))
                throw new IOException("OPC UA application certificate could not be created or validated");
            _opcUaClientConfiguration = config;
            return config;
        }
        finally { OpcUaConfigurationGate.Release(); }
    }
}
