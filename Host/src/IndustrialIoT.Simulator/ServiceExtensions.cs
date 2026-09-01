namespace IndustrialIoT.Simulator;

using System.Reflection;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.DependencyInjection;

public static class SimulatorServiceExtensions
{
    public static IServiceCollection AddSimulatorDrivers(this IServiceCollection services)
    {
        services.AddTransient<SimulatorDriver>();
        services.AddTransient<SimulationProfile>(_ => SimulationProfile.Default);
        return services;
    }
}
