using Opc.Ua;
using Opc.Ua.Server;

namespace MachineConnectionApi.Services;

public sealed partial class CsConnectivityService
{
    private sealed class CsOpcUaNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        : CustomNodeManager2(server, configuration, "urn:IndustrialIoT:CsConnectivity")
    {
        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                var namespaceIndex = NamespaceIndexes[0];
                var folder = new FolderState(null)
                {
                    NodeId = new("IOTT.Cs", namespaceIndex), BrowseName = new("IOTT.Cs", namespaceIndex),
                    DisplayName = "IOTT C/S protocol verification", TypeDefinitionId = ObjectTypeIds.FolderType,
                };
                folder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));
                var value = new BaseDataVariableState<double>(folder)
                {
                    NodeId = new("IOTT.Cs.Value", namespaceIndex), BrowseName = new("Value", namespaceIndex),
                    DisplayName = "Read/write test value", ReferenceTypeId = ReferenceTypeIds.Organizes,
                    TypeDefinitionId = VariableTypeIds.BaseDataVariableType, DataType = DataTypeIds.Double,
                    ValueRank = ValueRanks.Scalar, AccessLevel = AccessLevels.CurrentReadOrWrite,
                    UserAccessLevel = AccessLevels.CurrentReadOrWrite, Value = 0, StatusCode = Opc.Ua.StatusCodes.Good,
                    Timestamp = DateTime.UtcNow,
                };
                folder.AddChild(value);
                AddPredefinedNode(SystemContext, folder);
            }
        }
    }
}
