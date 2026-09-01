namespace IndustrialIoT.Protocols.NCLink;

using System.Text;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;
using ConnectionState = IndustrialIoT.Domain.Enums.ConnectionState;
using DataType = IndustrialIoT.Domain.Enums.DataType;

/// <summary>
/// OPC UA 协议驱动 — 适用于西门子 840Dsl、华中 HNC-848Di 等支持 OPC UA 的数控系统。
/// </summary>
[ProtocolDriver(ProtocolType.OpcUa, "Siemens", "西门子", "840Dsl", "HNC", "华中数控", "HNC-848Di")]
public sealed class OpcUaDriver : IProtocolDriver, IAddressSpaceBrowser
{
    private readonly ILogger<OpcUaDriver> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ISession? _session;
    private ConnectionState _state = ConnectionState.Disconnected;
    private static readonly (NodeId ReferenceTypeId, bool IncludeSubtypes)[] CompatibilityBrowseStrategies =
    [
        (NodeId.Null, false),
        (ReferenceTypeIds.Organizes, false),
        (ReferenceTypeIds.HasComponent, false),
        (ReferenceTypeIds.HasProperty, false),
    ];
    private sealed record OpcUaSecurityOptions(
        bool UseSecurity,
        bool AutoAcceptCerts,
        bool RejectSha1SignedCertificates,
        bool SuppressNonceValidationErrors,
        ushort MinimumCertificateKeySize);

    public ProtocolType Protocol => ProtocolType.OpcUa;
    public ConnectionState State => _state;
    public DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.Write
        | DriverCapabilities.Browse | DriverCapabilities.BatchRead;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public OpcUaDriver(ILogger<OpcUaDriver> logger)
    {
        _logger = logger;
    }

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        var oldState = _state;
        TransitionState(ConnectionState.Connecting);

        try
        {
            // Build endpoint URL: opc.tcp://host:port
            var port = config.Port > 0 ? config.Port : 4840;
            var endpointUrl = config.ExtendedProperties.TryGetValue("EndpointUrl", out var url)
                ? url
                : $"opc.tcp://{config.Host}:{port}";

            // Security options from config (default: security ON)
            var securityOptions = ResolveSecurityOptions(config);
            var useSecurity = securityOptions.UseSecurity;
            var autoAcceptCerts = securityOptions.AutoAcceptCerts;

            var certStoreRoot = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IndustrialIoT",
                "OpcUa");
            System.IO.Directory.CreateDirectory(certStoreRoot);
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(certStoreRoot, "own"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(certStoreRoot, "issuers"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(certStoreRoot, "trusted"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(certStoreRoot, "rejected"));

            // Application configuration
            var appConfig = new ApplicationConfiguration
            {
                ApplicationName = "IndustrialIoT_OpcUaClient",
                ApplicationUri = "urn:IndustrialIoT:OpcUaClient",
                ApplicationType = ApplicationType.Client,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = System.IO.Path.Combine(certStoreRoot, "own"),
                        SubjectName = "CN=IndustrialIoT_OpcUaClient",
                    },
                    TrustedIssuerCertificates = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = System.IO.Path.Combine(certStoreRoot, "issuers"),
                    },
                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = System.IO.Path.Combine(certStoreRoot, "trusted"),
                    },
                    RejectedCertificateStore = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = System.IO.Path.Combine(certStoreRoot, "rejected"),
                    },
                    AutoAcceptUntrustedCertificates = autoAcceptCerts,
                    RejectSHA1SignedCertificates = securityOptions.RejectSha1SignedCertificates,
                    MinimumCertificateKeySize = securityOptions.MinimumCertificateKeySize,
                    SuppressNonceValidationErrors = securityOptions.SuppressNonceValidationErrors,
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas
                {
                    OperationTimeout = (int)config.ConnectTimeout.TotalMilliseconds,
                    MaxMessageSize = 4 * 1024 * 1024,
                },
                ClientConfiguration = new ClientConfiguration
                {
                    DefaultSessionTimeout = 60_000,
                },
            };

            await appConfig.Validate(ApplicationType.Client);

            // When autoAcceptCerts is enabled, accept all certificate validation errors
            // including policy check failures (e.g. server cert with key < MinimumCertificateKeySize)
            if (autoAcceptCerts)
            {
                appConfig.CertificateValidator.CertificateValidation += (_, e) =>
                {
                    _logger.LogDebug("OPC UA accepting certificate: {Subject} (status: {Status})",
                        e.Certificate.Subject, e.Error.StatusCode);
                    e.Accept = true;
                };
            }

            // Ensure application certificate exists and meets minimum key length (Siemens 840Dsl requires ≥2048)
            var certId = appConfig.SecurityConfiguration.ApplicationCertificate;
            var appCert = await certId.Find(true);
            if (appCert != null && X509Utils.GetRSAPublicKeySize(appCert) < 2048)
            {
                _logger.LogWarning("OPC UA client certificate key size {KeySize} < 2048, regenerating",
                    X509Utils.GetRSAPublicKeySize(appCert));
                var certStore = certId.OpenStore();
                try { await certStore.DeleteAsync(appCert.Thumbprint, ct); } finally { certStore.Close(); }
                appCert = null;
            }
            if (appCert == null)
            {
                appCert = CertificateFactory.CreateCertificate(
                    appConfig.ApplicationUri,
                    appConfig.ApplicationName,
                    certId.SubjectName,
                    null)
                    .SetLifeTime(300)
                    .SetRSAKeySize(2048)
                    .CreateForRSA();
                X509Utils.AddToStore(appCert, certId.StoreType, certId.StorePath, null);
            }

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(config.ConnectTimeout);

            // TCP pre-check: OPC UA SDK's Session.Create does not properly respect
            // CancellationToken during TCP connect — Windows TCP SYN retransmission
            // can hang 60-80s. Do a quick TCP probe first to fail fast.
            var tcpPort = new Uri(endpointUrl).Port;
            using (var tcpProbe = new TcpClient())
            {
                try
                {
                    var tcpTimeout = TimeSpan.FromSeconds(Math.Min(config.ConnectTimeout.TotalSeconds, 5));
                    await tcpProbe.ConnectAsync(config.Host, tcpPort > 0 ? tcpPort : port, connectCts.Token).AsTask()
                        .WaitAsync(tcpTimeout, ct);
                }
                catch (Exception ex) when (ex is TimeoutException or SocketException or OperationCanceledException)
                {
                    throw new TimeoutException(
                        $"TCP connection to {config.Host}:{(tcpPort > 0 ? tcpPort : port)} unreachable (probe failed in <5s)", ex);
                }
            }

            // Select endpoint — respect security config
            var selectedEndpoint = await Task.Run(
                () => CoreClientUtils.SelectEndpoint(appConfig, endpointUrl, useSecurity: useSecurity),
                connectCts.Token).WaitAsync(config.ConnectTimeout, ct);
            var endpointConfig = EndpointConfiguration.Create(appConfig);
            var endpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfig);

            _logger.LogInformation(
                "OPC UA connecting to {Endpoint} (useSecurity={UseSecurity}, autoAcceptCerts={AutoAccept}, rejectSha1={RejectSha1}, suppressNonce={SuppressNonce})",
                endpointUrl, useSecurity, autoAcceptCerts,
                securityOptions.RejectSha1SignedCertificates, securityOptions.SuppressNonceValidationErrors);

            // User identity
            var identity = BuildUserIdentity(config);

            // Create session — WaitAsync as safety net because SDK may ignore
            // CancellationToken internally; ContinueWith cleans leaked sessions.
            var sessionTask = Session.Create(
                appConfig,
                endpoint,
                updateBeforeConnect: false,
                sessionName: $"IIoT_{Guid.NewGuid():N}",
                sessionTimeout: 60_000,
                identity,
                preferredLocales: null,
                connectCts.Token);

            try
            {
                _session = await sessionTask.WaitAsync(config.ConnectTimeout, ct);
            }
            catch (TimeoutException)
            {
                // Session.Create may still complete in background — dispose to avoid socket leak
                _ = sessionTask.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        try { t.Result?.Dispose(); }
                        catch { /* best effort cleanup */ }
                    }
                }, TaskScheduler.Default);
                throw;
            }

            _session.KeepAlive += OnKeepAlive;

            TransitionState(ConnectionState.Connected);
            _logger.LogInformation("OPC UA connected to {Endpoint}", endpointUrl);
            return new ConnectionResult { Success = true };
        }
        catch (Exception ex)
        {
            TransitionState(ConnectionState.Faulted);
            _logger.LogError(ex, "OPC UA connection failed to {Host}:{Port}", config.Host, config.Port);
            return new ConnectionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_session is { Connected: true })
        {
            _session.KeepAlive -= OnKeepAlive;
            await _session.CloseAsync(ct);
            _session.Dispose();
            _session = null;
        }
        TransitionState(ConnectionState.Disconnected);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_session is not { Connected: true })
            return false;

        await _lock.WaitAsync(ct);
        try
        {
            if (_session is not { Connected: true } session)
                return false;

            var result = await session.ReadValueAsync(VariableIds.Server_ServerStatus_State, ct);
            var ok = StatusCode.IsGood(result.StatusCode);
            if (!ok)
                TransitionState(ConnectionState.Faulted);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OPC UA ping failed");
            TransitionState(ConnectionState.Faulted);
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        EnsureConnected();
        await _lock.WaitAsync(ct);
        try
        {
            var nodeId = ParseNodeId(address);
            var readResult = await _session!.ReadValueAsync(nodeId, ct);

            return new TagValue
            {
                Address = address,
                DataType = dataType,
                Value = ConvertValue(readResult.Value, dataType),
                Quality = StatusCode.IsGood(readResult.StatusCode)
                    ? Domain.Enums.TagQuality.Good
                    : Domain.Enums.TagQuality.Bad,
                Timestamp = readResult.SourceTimestamp != DateTime.MinValue
                    ? new DateTimeOffset(readResult.SourceTimestamp, TimeSpan.Zero)
                    : DateTimeOffset.UtcNow,
                ErrorMessage = StatusCode.IsGood(readResult.StatusCode) ? null : readResult.StatusCode.ToString(),
            };
        }
        catch (Exception ex)
        {
            return new TagValue
            {
                Address = address,
                DataType = dataType,
                Value = 0,
                Quality = Domain.Enums.TagQuality.Bad,
                Timestamp = DateTimeOffset.UtcNow,
                ErrorMessage = ex.Message,
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(
        IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        EnsureConnected();
        await _lock.WaitAsync(ct);
        try
        {
            var nodesToRead = new ReadValueIdCollection();
            foreach (var req in requests)
            {
                nodesToRead.Add(new ReadValueId
                {
                    NodeId = ParseNodeId(req.Address),
                    AttributeId = Attributes.Value,
                });
            }

            var readResponse = await _session!.ReadAsync(null, 0, TimestampsToReturn.Both, nodesToRead, ct);
            var results = readResponse.Results;

            var tags = new List<TagValue>(requests.Count);
            for (var i = 0; i < requests.Count; i++)
            {
                var req = requests[i];
                var dv = results[i];
                tags.Add(new TagValue
                {
                    Address = req.Address,
                    DataType = req.DataType,
                    Value = ConvertValue(dv.Value, req.DataType),
                    Quality = StatusCode.IsGood(dv.StatusCode)
                        ? Domain.Enums.TagQuality.Good
                        : Domain.Enums.TagQuality.Bad,
                    Timestamp = dv.SourceTimestamp != DateTime.MinValue
                        ? new DateTimeOffset(dv.SourceTimestamp, TimeSpan.Zero)
                        : DateTimeOffset.UtcNow,
                    ErrorMessage = StatusCode.IsGood(dv.StatusCode) ? null : dv.StatusCode.ToString(),
                });
            }
            return tags;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<WriteResult> WriteTagAsync(
        string address, DataType dataType, object value, CancellationToken ct = default)
    {
        EnsureConnected();
        await _lock.WaitAsync(ct);
        try
        {
            var nodeId = ParseNodeId(address);
            var writeValue = new WriteValue
            {
                NodeId = nodeId,
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(value)),
            };

            var writeResponse = await _session!.WriteAsync(null, new WriteValueCollection { writeValue }, ct);
            var writeResults = writeResponse.Results;

            var ok = StatusCode.IsGood(writeResults[0]);
            return new WriteResult
            {
                Success = ok,
                ErrorMessage = ok ? null : writeResults[0].ToString(),
            };
        }
        catch (Exception ex)
        {
            return new WriteResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<AddressNode>> BrowseAsync(
        string? parentPath = null, CancellationToken ct = default)
    {
        EnsureConnected();
        await _lock.WaitAsync(ct);
        try
        {
            var startNodeId = string.IsNullOrEmpty(parentPath)
                ? ObjectIds.ObjectsFolder
                : ParseNodeId(parentPath);

            var browseRefs = await BrowseReferencesAsync(startNodeId, ct);
            var filteredRefs = browseRefs
                .Where(rd => rd.NodeClass is NodeClass.Variable or NodeClass.Object)
                .ToList();

            var nodes = new List<AddressNode>();
            if (filteredRefs.Count > 0)
            {
                // Batch-read DataType + AccessLevel for variable nodes
                var variableRefs = filteredRefs
                    .Where(rd => rd.NodeClass == NodeClass.Variable)
                    .ToList();

                var readResults = new Dictionary<string, (DataType dt, bool readable, bool writable)>();
                if (variableRefs.Count > 0)
                {
                    var nodesToRead = new ReadValueIdCollection();
                    foreach (var rd in variableRefs)
                    {
                        var nodeId = ExpandedNodeId.ToNodeId(rd.NodeId, _session.NamespaceUris);
                        // Read DataType attribute
                        nodesToRead.Add(new ReadValueId
                        {
                            NodeId = nodeId,
                            AttributeId = Attributes.DataType,
                        });
                        // Read AccessLevel attribute
                        nodesToRead.Add(new ReadValueId
                        {
                            NodeId = nodeId,
                            AttributeId = Attributes.UserAccessLevel,
                        });
                    }

                    var readResponse = await _session.ReadAsync(null, 0, TimestampsToReturn.Neither, nodesToRead, ct);
                    var results = readResponse.Results;

                    for (int i = 0; i < variableRefs.Count; i++)
                    {
                        var rd = variableRefs[i];
                        var dtResult = results[i * 2];
                        var accessResult = results[i * 2 + 1];

                        var dataType = DataType.Double; // default
                        if (StatusCode.IsGood(dtResult.StatusCode) && dtResult.Value is NodeId dtNodeId)
                        {
                            dataType = MapOpcUaDataType(dtNodeId);
                        }

                        bool readable = true, writable = false;
                        if (StatusCode.IsGood(accessResult.StatusCode) && accessResult.Value is byte accessLevel)
                        {
                            readable = (accessLevel & AccessLevels.CurrentRead) != 0;
                            writable = (accessLevel & AccessLevels.CurrentWrite) != 0;
                        }

                        readResults[rd.NodeId.ToString()] = (dataType, readable, writable);
                    }
                }

                foreach (var rd in filteredRefs)
                {
                    var isFolder = rd.NodeClass == NodeClass.Object;
                    var nodeKey = rd.NodeId.ToString();

                    if (isFolder)
                    {
                        nodes.Add(new AddressNode
                        {
                            Path = nodeKey,
                            DisplayName = rd.DisplayName?.Text ?? rd.BrowseName.Name,
                            NodeType = AddressNodeType.Folder,
                            DataType = null,
                            IsReadable = false,
                            IsWritable = false,
                        });
                    }
                    else
                    {
                        var (dt, readable, writable) = readResults.GetValueOrDefault(nodeKey,
                            (DataType.Double, true, false));
                        nodes.Add(new AddressNode
                        {
                            Path = nodeKey,
                            DisplayName = rd.DisplayName?.Text ?? rd.BrowseName.Name,
                            NodeType = AddressNodeType.Variable,
                            DataType = dt,
                            IsReadable = readable,
                            IsWritable = writable,
                        });
                    }
                }
            }
            return nodes;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Stream> ExportAddressSpaceAsync(
        Domain.Enums.ExportFormat format, CancellationToken ct = default)
    {
        EnsureConnected();
        var sb = new StringBuilder();
        sb.AppendLine("Path,DisplayName,NodeType,DataType,Readable,Writable");
        await ExportRecursiveAsync(sb, null, 0, ct);
        Stream stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        return stream;
    }

    private async Task ExportRecursiveAsync(StringBuilder sb, string? parentPath, int depth, CancellationToken ct)
    {
        if (depth > 10) return; // prevent infinite recursion on cyclic references

        var nodes = await BrowseAsync(parentPath, ct);
        foreach (var node in nodes)
        {
            if (node.NodeType == AddressNodeType.Variable)
            {
                sb.AppendLine(
                    $"\"{node.Path}\",\"{node.DisplayName}\",{node.NodeType},{node.DataType},{node.IsReadable},{node.IsWritable}");
            }
            else
            {
                // Recurse into folders
                await ExportRecursiveAsync(sb, node.Path, depth + 1, ct);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _lock.Dispose();
    }

    // === Private helpers ===

    private void TransitionState(ConnectionState newState)
    {
        var old = _state;
        _state = newState;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs
        {
            OldState = old,
            NewState = newState,
        });
    }

    private void EnsureConnected()
    {
        if (_session is null || !_session.Connected)
            throw new InvalidOperationException("OPC UA session not connected");
    }

    private static UserIdentity BuildUserIdentity(DeviceConnectionConfig config) =>
        !string.IsNullOrEmpty(config.Username)
            ? new UserIdentity(config.Username, Encoding.UTF8.GetBytes(config.Password ?? ""))
            : new UserIdentity();

    private async Task<ReferenceDescriptionCollection> BrowseReferencesAsync(NodeId startNodeId, CancellationToken ct)
    {
        try
        {
            return await BrowseByReferenceTypeAsync(startNodeId, ReferenceTypeIds.HierarchicalReferences, true, ct);
        }
        catch (ServiceResultException ex) when (ex.StatusCode == StatusCodes.BadInternalError)
        {
            _logger.LogWarning(ex,
                "OPC UA browse failed with HierarchicalReferences for {NodeId}; retrying with compatibility reference types",
                startNodeId);

            var references = new ReferenceDescriptionCollection();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var strategy in CompatibilityBrowseStrategies)
            {
                try
                {
                    foreach (var reference in await BrowseByReferenceTypeAsync(
                        startNodeId, strategy.ReferenceTypeId, strategy.IncludeSubtypes, ct))
                    {
                        if (seen.Add(reference.NodeId.ToString()))
                            references.Add(reference);
                    }
                }
                catch (ServiceResultException fallbackEx) when (fallbackEx.StatusCode == StatusCodes.BadInternalError)
                {
                    _logger.LogDebug(fallbackEx, "OPC UA compatibility browse failed for {NodeId} with {ReferenceType}",
                        startNodeId, strategy.ReferenceTypeId);
                }
            }

            if (references.Count > 0)
                return references;

            throw;
        }
    }

    private async Task<ReferenceDescriptionCollection> BrowseByReferenceTypeAsync(
        NodeId startNodeId, NodeId referenceTypeId, bool includeSubtypes, CancellationToken ct)
    {
        var (_, _, browseRefs) = await _session!.BrowseAsync(null, null, startNodeId,
            0u, BrowseDirection.Forward, referenceTypeId,
            includeSubtypes, 0u, ct);
        return browseRefs ?? [];
    }

    private static OpcUaSecurityOptions ResolveSecurityOptions(DeviceConnectionConfig config)
    {
        var useSecurity = !TryGetBoolean(config.ExtendedProperties, "UseSecurity", out var secBool) || secBool;
        var autoAcceptCerts = TryGetBoolean(config.ExtendedProperties, "AutoAcceptUntrustedCerts", out var acceptBool)
            && acceptBool;
        var rejectSha1SignedCertificates =
            TryGetBoolean(config.ExtendedProperties, "RejectSHA1SignedCertificates", out var rejectSha1)
                ? rejectSha1
                : !autoAcceptCerts;
        var suppressNonceValidationErrors =
            TryGetBoolean(config.ExtendedProperties, "SuppressNonceValidationErrors", out var suppressNonce)
                ? suppressNonce
                : autoAcceptCerts;

        return new OpcUaSecurityOptions(
            useSecurity,
            autoAcceptCerts,
            rejectSha1SignedCertificates,
            suppressNonceValidationErrors,
            autoAcceptCerts ? (ushort)1024 : (ushort)2048);
    }

    private static bool TryGetBoolean(IReadOnlyDictionary<string, string> properties, string key, out bool value)
    {
        value = false;
        return properties.TryGetValue(key, out var raw) && bool.TryParse(raw, out value);
    }

    private static NodeId ParseNodeId(string address)
    {
        // Support formats:
        //   "ns=2;s=Channel1.Device1.Tag1"  — standard OPC UA NodeId
        //   "ns=2;i=1234"                   — numeric NodeId
        //   "2:Channel1.Device1.Tag1"        — shorthand ns:identifier
        //   Plain string                     — assume ns=2, string identifier
        if (address.StartsWith("ns=", StringComparison.OrdinalIgnoreCase) ||
            address.StartsWith("i=", StringComparison.OrdinalIgnoreCase) ||
            address.StartsWith("s=", StringComparison.OrdinalIgnoreCase))
        {
            return NodeId.Parse(address);
        }

        if (address.Contains(':'))
        {
            var parts = address.Split(':', 2);
            if (ushort.TryParse(parts[0], out var ns))
                return new NodeId(parts[1], ns);
        }

        // Default: namespace 2, string identifier
        return new NodeId(address, 2);
    }

    private static object ConvertValue(object? raw, DataType target)
    {
        if (raw is null) return 0;
        return target switch
        {
            DataType.Bool => Convert.ToBoolean(raw),
            DataType.Int16 => Convert.ToInt16(raw),
            DataType.Int32 => Convert.ToInt32(raw),
            DataType.Int64 => Convert.ToInt64(raw),
            DataType.UInt16 => Convert.ToUInt16(raw),
            DataType.UInt32 => Convert.ToUInt32(raw),
            DataType.Float => Convert.ToSingle(raw),
            DataType.Double => Convert.ToDouble(raw),
            DataType.String => raw.ToString() ?? "",
            _ => raw,
        };
    }

    /// <summary>
    /// Map OPC UA built-in DataType NodeId to domain DataType enum.
    /// </summary>
    private static DataType MapOpcUaDataType(NodeId dataTypeId)
    {
        // OPC UA built-in type NodeIds are in namespace 0
        if (dataTypeId.NamespaceIndex != 0 || dataTypeId.IdType != IdType.Numeric)
            return DataType.Double;

        return (uint)dataTypeId.Identifier switch
        {
            DataTypes.Boolean => DataType.Bool,
            DataTypes.SByte or DataTypes.Int16 => DataType.Int16,
            DataTypes.Byte or DataTypes.UInt16 => DataType.UInt16,
            DataTypes.Int32 => DataType.Int32,
            DataTypes.UInt32 => DataType.UInt32,
            DataTypes.Int64 => DataType.Int64,
            DataTypes.UInt64 => DataType.String,
            DataTypes.Float => DataType.Float,
            DataTypes.Double => DataType.Double,
            DataTypes.ByteString => DataType.ByteArray,
            DataTypes.String or DataTypes.LocalizedText => DataType.String,
            _ => DataType.Double,
        };
    }

    private void OnKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        if (e.Status != null && ServiceResult.IsNotGood(e.Status))
        {
            _logger.LogWarning("OPC UA keep-alive failed: {Status}", e.Status);
            if (_state == ConnectionState.Connected)
                TransitionState(ConnectionState.Reconnecting);
        }
        else if (_state == ConnectionState.Reconnecting)
        {
            TransitionState(ConnectionState.Connected);
        }
    }
}
