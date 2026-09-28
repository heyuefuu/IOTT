using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Host;
using IndustrialIoT.Protocols.FANUC;
using IndustrialIoT.Protocols.Haas;
using IndustrialIoT.Protocols.MTConnect;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.DependencyInjection;

internal static class CncBrandRegistrationRegressionTests
{
    public static Task RunAsync()
    {
        var registry = new DriverRegistry();
        ProtocolDriverRegistration.RegisterRealDrivers(new ServiceCollection(), registry);
        TestSupport.Require(registry.Resolve(ProtocolType.HaasMdc, "哈斯（Haas）", "VF-2") == typeof(HaasMdcDriver),
            "UI Haas brand must resolve");
        TestSupport.Require(registry.Resolve(ProtocolType.FOCAS, "牧野（Makino）", "a61nx") == typeof(FocasDriver),
            "UI Makino brand must resolve");
        TestSupport.Require(registry.Resolve(ProtocolType.MTConnect, "马扎克（Mazak）", "Nexus 6800") == typeof(MTConnectDriver),
            "Mazak wildcard fallback must remain available");
        TestSupport.Require(registry.Resolve(ProtocolType.HaasMdc, "haas") == typeof(HaasMdcDriver),
            "ASCII case insensitive brand matching changed");
        TestSupport.Require(registry.Resolve(ProtocolType.HaasMdc, "unregistered") is null,
            "Unknown brand must not gain a Haas fallback");
        registry.Register(typeof(MTConnectDriver), ProtocolType.HaasMdc, ["MODEL-OVERRIDE"]);
        TestSupport.Require(registry.Resolve(ProtocolType.HaasMdc, "哈斯（Haas）", "MODEL-OVERRIDE") == typeof(MTConnectDriver),
            "Model registration must retain priority over brand aliases");
        return Task.CompletedTask;
    }
}
