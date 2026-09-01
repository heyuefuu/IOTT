namespace IndustrialIoT.Protocols.Models;

using IndustrialIoT.Domain.Enums;

public record AddressNode
{
    public required string Path { get; init; }
    public required string DisplayName { get; init; }
    public required AddressNodeType NodeType { get; init; }
    public DataType? DataType { get; init; }
    public bool IsReadable { get; init; }
    public bool IsWritable { get; init; }
    public IReadOnlyList<AddressNode>? Children { get; init; }
}

public enum AddressNodeType { Folder, Variable }
