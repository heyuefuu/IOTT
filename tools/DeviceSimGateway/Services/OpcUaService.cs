namespace DeviceSimGateway.Services;

using DeviceSimGateway.Core;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

/// <summary>基于官方 UA-.NETStandard 实现的 OPC UA 服务端（匿名、SecurityPolicy=None）。
/// 暴露 X/Y/Z/Spindle/Feed/PartCount，值绑定共享数据区并定时刷新。</summary>
public sealed class OpcUaService : IDeviceService
{
    public string Key => "opcua";
    public string Name => "OPC UA";
    public string Description => "OPC UA Server（匿名/None，暴露 X/Y/Z/S/F/计数）";
    public int DefaultPort => 4840;
    public bool Implemented => true;
    public ServiceState State { get; private set; } = ServiceState.Stopped;
    public string Message { get; private set; } = "";
    public int Port { get; private set; }

    private ApplicationInstance? _app;
    private SimServer? _server;

    public async Task StartAsync(int port, SimDataStore data, GeneratorConfig gen, CancellationToken ct)
    {
        Port = port;
        var pki = Path.Combine(AppContext.BaseDirectory, "pki");
        var config = new ApplicationConfiguration
        {
            ApplicationName = "DeviceSimGateway",
            ApplicationUri = "urn:DeviceSimGateway:Sim",
            ProductUri = "urn:DeviceSimGateway",
            ApplicationType = ApplicationType.Server,
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = { $"opc.tcp://0.0.0.0:{port}/DeviceSimGateway" },
                SecurityPolicies =
                {
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None },
                },
                UserTokenPolicies = { new UserTokenPolicy(UserTokenType.Anonymous) },
                DiagnosticsEnabled = false,
                MaxSessionCount = 100,
                MinSessionTimeout = 10000,
                MaxSessionTimeout = 3600000,
                MaxBrowseContinuationPoints = 10,
                MaxQueryContinuationPoints = 10,
                MaxHistoryContinuationPoints = 100,
                MinSubscriptionLifetime = 10000,
            },
            SecurityConfiguration = new SecurityConfiguration
            {
                AutoAcceptUntrustedCertificates = true,
                AddAppCertToTrustedStore = true,
                ApplicationCertificate = new CertificateIdentifier { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "own"), SubjectName = "CN=DeviceSimGateway, O=Sim" },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "issuer") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "rejected") },
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            TraceConfiguration = new TraceConfiguration(),
        };

        await config.Validate(ApplicationType.Server);
        config.CertificateValidator.CertificateValidation += (_, e) => e.Accept = true;

        _app = new ApplicationInstance { ApplicationName = "DeviceSimGateway", ApplicationType = ApplicationType.Server, ApplicationConfiguration = config };
        await _app.CheckApplicationInstanceCertificate(false, 2048);

        _server = new SimServer(data);
        try { await _app.Start(_server); }
        catch (Exception ex)
        {
            var sre = ex as Opc.Ua.ServiceResultException;
            Message = "启动失败: " + ex.Message
                + " | status=" + (sre?.StatusCode.ToString() ?? "-")
                + " | info=" + (sre?.Result?.AdditionalInfo ?? "-")
                + " | innerResult=" + (sre?.Result?.InnerResult?.ToString() ?? "-")
                + " | inner=" + (ex.InnerException?.Message ?? "-");
            throw new InvalidOperationException(Message, ex);
        }

        State = ServiceState.Running;
        Message = $"opc.tcp://0.0.0.0:{port}/DeviceSimGateway（匿名）";
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_server is not null) { try { _server.Stop(); } catch { } _server = null; }
        _app = null;
        if (State == ServiceState.Running) { State = ServiceState.Stopped; Message = "已停止"; }
    }
}

/// <summary>StandardServer，挂载自定义地址空间。</summary>
internal sealed class SimServer : StandardServer
{
    private readonly SimDataStore _store;
    public SimServer(SimDataStore store) => _store = store;

    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
    {
        var nodeManagers = new List<INodeManager> { new SimNodeManager(server, configuration, _store) };
        return new MasterNodeManager(server, configuration, null, nodeManagers.ToArray());
    }
}

/// <summary>地址空间：/Objects/Sim/{X,Y,Z,Spindle,Feed,PartCount}，绑定共享数据区并定时刷新。</summary>
internal sealed class SimNodeManager : CustomNodeManager2
{
    private readonly SimDataStore _store;
    private readonly List<(BaseDataVariableState Var, Func<double> Get, Func<object, object> Type)> _vars = new();
    private Timer? _timer;

    public SimNodeManager(IServerInternal server, ApplicationConfiguration configuration, SimDataStore store)
        : base(server, configuration, "urn:DeviceSimGateway:Sim")
    {
        _store = store;
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            var folder = new FolderState(null)
            {
                SymbolicName = "Sim",
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = ObjectTypeIds.FolderType,
                NodeId = new NodeId("Sim", NamespaceIndex),
                BrowseName = new QualifiedName("Sim", NamespaceIndex),
                DisplayName = new LocalizedText("Sim"),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                EventNotifier = EventNotifiers.None,
            };
            if (externalReferences?.TryGetValue(ObjectIds.ObjectsFolder, out var refs) == true)
                refs!.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));
            AddPredefinedNode(SystemContext, folder);

            AddVar(folder, "X", DataTypeIds.Double, () => Reg(_store.Holding, 0) / 100.0);
            AddVar(folder, "Y", DataTypeIds.Double, () => Reg(_store.Holding, 1) / 100.0);
            AddVar(folder, "Z", DataTypeIds.Double, () => Reg(_store.Holding, 2) / 100.0);
            AddVar(folder, "Spindle", DataTypeIds.Double, () => (double)Reg(_store.Input, 0));
            AddVar(folder, "Feed", DataTypeIds.Double, () => Reg(_store.Input, 1) / 10.0);
            AddVar(folder, "PartCount", DataTypeIds.Int32, () => (double)Reg(_store.Holding, 3));

            _timer = new Timer(_ => Update(), null, 0, 500);
        }
    }

    private void AddVar(NodeState parent, string name, NodeId dataType, Func<double> get)
    {
        var v = new BaseDataVariableState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = new NodeId($"Sim.{name}", NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText(name),
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            Historizing = false,
            Value = dataType == DataTypeIds.Int32 ? 0 : 0.0,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow,
        };
        parent.AddChild(v);
        AddPredefinedNode(SystemContext, v);
        _vars.Add((v, get, dataType == DataTypeIds.Int32 ? (x => Convert.ToInt32(x)) : (x => x)));
    }

    private void Update()
    {
        try
        {
            lock (Lock)
            {
                var fail = _store.Failure.ShouldFail();
                foreach (var (v, get, type) in _vars)
                {
                    v.Value = type(get());
                    // 随机失败：该刷新周期把节点质量置为 Bad，客户端读到 Bad 质量而非数值。
                    v.StatusCode = fail ? StatusCodes.Bad : StatusCodes.Good;
                    v.Timestamp = DateTime.UtcNow;
                    v.ClearChangeMasks(SystemContext, false);
                }
            }
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer?.Dispose(); _timer = null; }
        base.Dispose(disposing);
    }

    private static ushort Reg(ushort[] a, int i) => i >= 0 && i < a.Length ? a[i] : (ushort)0;
}
