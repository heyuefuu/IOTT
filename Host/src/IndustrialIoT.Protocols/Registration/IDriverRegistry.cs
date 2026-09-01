namespace IndustrialIoT.Protocols.Registration;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Abstractions;

public interface IDriverRegistry
{
    void Register(Type driverType, ProtocolType protocol, string[] brands, int priority = 0);
    /// <summary>
    /// Resolve driver type. Resolution order:
    /// 1. Exact model match → 2. Exact brand match → 3. Wildcard "*" fallback
    /// </summary>
    Type? Resolve(ProtocolType protocol, string brand, string? model = null);
    IReadOnlyList<ProtocolType> GetSupportedProtocols();
    IReadOnlyList<string> GetSupportedBrands(ProtocolType protocol);
}

public interface IProtocolDriverFactory
{
    IProtocolDriver Create(ProtocolType protocol, string brand, string? model = null);
}
