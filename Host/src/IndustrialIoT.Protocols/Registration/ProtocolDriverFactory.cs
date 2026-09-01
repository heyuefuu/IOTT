namespace IndustrialIoT.Protocols.Registration;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public class ProtocolDriverFactory : IProtocolDriverFactory
{
    private readonly IDriverRegistry _registry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProtocolDriverFactory> _logger;

    public ProtocolDriverFactory(IDriverRegistry registry, IServiceProvider serviceProvider, ILogger<ProtocolDriverFactory> logger)
    {
        _registry = registry;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public IProtocolDriver Create(ProtocolType protocol, string brand, string? model = null)
    {
        var driverType = _registry.Resolve(protocol, brand, model)
            ?? throw new InvalidOperationException($"No driver registered for protocol={protocol}, brand={brand}, model={model}");

        _logger.LogInformation("Creating driver {DriverType} for {Protocol}/{Brand}/{Model}", driverType.Name, protocol, brand, model);
        return (IProtocolDriver)ActivatorUtilities.CreateInstance(_serviceProvider, driverType);
    }
}
