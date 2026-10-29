using System.Globalization;
using System.Text;
using System.Text.Json;
using MachineConnectionApi.Models;
using Opc.Ua;
using Opc.Ua.Client;

namespace MachineConnectionApi.Services;

public sealed partial class CsConnectivityService
{
    private sealed record OpcUaProbeSettings(string? EndpointUrl = null, string NodeId = "i=2259", bool UseSecurity = false);

    private static async Task<string> ReadOpcUaAsync(CsGateway gateway, string? configuration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var settings = string.IsNullOrWhiteSpace(configuration) ? new OpcUaProbeSettings()
            : JsonSerializer.Deserialize<OpcUaProbeSettings>(configuration, ConfigurationJsonOptions)
                ?? throw new ArgumentException("OPC UA probe configuration is empty");
        var endpointUrl = settings.EndpointUrl ?? new UriBuilder("opc.tcp", gateway.Ip, gateway.Port).Uri.AbsoluteUri;
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri) || uri.Scheme != "opc.tcp"
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.Fragment.Length != 0)
            throw new ArgumentException("OPC UA EndpointUrl must be an opc.tcp URL without credentials or fragment");
        if (!settings.UseSecurity && !string.IsNullOrEmpty(gateway.Username))
            throw new ArgumentException("OPC UA username/password requires UseSecurity=true and trusted server certificates");
        NodeId node;
        try { node = NodeId.Parse(settings.NodeId); }
        catch (Exception error) when (error is FormatException or ServiceResultException) { throw new ArgumentException("Invalid OPC UA NodeId", error); }
        try
        {
            var config = await OpcUaClientConfigurationAsync(ct);
            var endpoint = await CoreClientUtils.SelectEndpointAsync(config, uri.AbsoluteUri, settings.UseSecurity,
                3000, OpcUaTelemetry, ct) ?? throw new IOException("No compatible OPC UA endpoint was discovered");
            if (settings.UseSecurity && endpoint.SecurityMode == MessageSecurityMode.None)
                throw new IOException("OPC UA endpoint did not negotiate the requested security mode");
            var configured = new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(config));
            IUserIdentity identity = string.IsNullOrEmpty(gateway.Username) ? new UserIdentity(new AnonymousIdentityToken())
                : new UserIdentity(gateway.Username, Encoding.UTF8.GetBytes(gateway.Password ?? ""));
            var connect = new DefaultSessionFactory(OpcUaTelemetry).CreateAsync(config, configured, false,
                "IOTT-CS-" + Guid.NewGuid().ToString("N"), 30_000, identity, null, ct);
            Opc.Ua.Client.ISession session;
            try { session = await connect.WaitAsync(ct); }
            catch
            {
                _ = connect.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; }, TaskScheduler.Default);
                throw;
            }
            using (session)
            {
                try
                {
                    var response = await session.ReadAsync(null, 0, TimestampsToReturn.Both,
                        new ReadValueIdCollection { new() { NodeId = node, AttributeId = Attributes.Value } }, ct);
                    if (response.Results.Count != 1 || StatusCode.IsBad(response.Results[0].StatusCode))
                        throw new IOException($"OPC UA Read {node}: {(response.Results.Count == 0 ? "empty result" : response.Results[0].StatusCode)}");
                    var value = Convert.ToString(response.Results[0].Value, CultureInfo.InvariantCulture);
                    return $"OPC UA Read {node}={value}; quality={response.Results[0].StatusCode}; SecurityMode={endpoint.SecurityMode}; Policy={endpoint.SecurityPolicyUri}";
                }
                finally
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    try { await session.CloseAsync(cleanup.Token); }
                    catch (Exception error) when (error is ServiceResultException or OperationCanceledException) { }
                }
            }
        }
        catch (ServiceResultException error) { throw new IOException("OPC UA service failed: " + error.Message, error); }
    }
}
