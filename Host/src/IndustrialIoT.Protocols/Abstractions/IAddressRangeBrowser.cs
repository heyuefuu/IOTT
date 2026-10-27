namespace IndustrialIoT.Protocols.Abstractions;

using IndustrialIoT.Domain.ValueObjects;

/// <summary>A declared address catalog that can be created without opening a device connection.</summary>
public interface IAddressRangeBrowser : IAddressSpaceBrowser
{
    IAddressSpaceBrowser CreateAddressSpaceBrowser(DeviceConnectionConfig config, string? model = null);
}
