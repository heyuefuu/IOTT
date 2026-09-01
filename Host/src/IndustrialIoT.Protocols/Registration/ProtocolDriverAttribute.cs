namespace IndustrialIoT.Protocols.Registration;

using IndustrialIoT.Domain.Enums;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class ProtocolDriverAttribute : Attribute
{
    public ProtocolType Protocol { get; }
    public string[] SupportedBrands { get; }
    public int Priority { get; set; }

    public ProtocolDriverAttribute(ProtocolType protocol, params string[] supportedBrands)
    {
        Protocol = protocol;
        SupportedBrands = supportedBrands;
    }
}
