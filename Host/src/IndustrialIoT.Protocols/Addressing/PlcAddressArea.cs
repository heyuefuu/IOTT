namespace IndustrialIoT.Protocols.Addressing;

using IndustrialIoT.Domain.Enums;

public sealed record PlcAddressArea(
    string Id, string DisplayName, int Start, int End, DataType DataType,
    Func<int, string> Address, bool IsWritable = true);

public sealed class AddressSpaceConfigurationException(string message) : ArgumentException(message);
