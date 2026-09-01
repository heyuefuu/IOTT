namespace IndustrialIoT.Protocols.Registration;

using System.Collections.Concurrent;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Abstractions;

public class DriverRegistry : IDriverRegistry
{
    private readonly ConcurrentDictionary<(ProtocolType Protocol, string Brand), (Type DriverType, int Priority)> _registry = new();

    public void Register(Type driverType, ProtocolType protocol, string[] brands, int priority = 0)
    {
        if (!typeof(IProtocolDriver).IsAssignableFrom(driverType))
            throw new ArgumentException($"Type {driverType.Name} does not implement IProtocolDriver");

        foreach (var brand in brands)
        {
            var key = (protocol, brand.ToUpperInvariant());
            _registry.AddOrUpdate(key,
                (driverType, priority),
                (_, existing) => priority > existing.Priority ? (driverType, priority) : existing);
        }
    }

    public Type? Resolve(ProtocolType protocol, string brand, string? model = null)
    {
        // 1. Try exact model match (most specific)
        if (!string.IsNullOrEmpty(model) &&
            _registry.TryGetValue((protocol, model.ToUpperInvariant()), out var entry))
            return entry.DriverType;

        // 2. Try exact brand match
        if (_registry.TryGetValue((protocol, brand.ToUpperInvariant()), out entry))
            return entry.DriverType;

        // 3. Try wildcard
        if (_registry.TryGetValue((protocol, "*"), out entry))
            return entry.DriverType;

        return null;
    }

    public IReadOnlyList<ProtocolType> GetSupportedProtocols() =>
        _registry.Keys.Select(k => k.Protocol).Distinct().ToList();

    public IReadOnlyList<string> GetSupportedBrands(ProtocolType protocol) =>
        _registry.Keys.Where(k => k.Protocol == protocol).Select(k => k.Brand).ToList();
}
