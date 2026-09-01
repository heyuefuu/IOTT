namespace IndustrialIoT.Protocols.MTConnect;

using IndustrialIoT.Domain.ValueObjects;

public sealed record MTConnectWriteAdapterOptions
{
    public string? EndpointUrl { get; init; }
    public string? BearerToken { get; init; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(EndpointUrl);

    public static MTConnectWriteAdapterOptions From(DeviceConnectionConfig config) => new()
    {
        EndpointUrl = config.ExtendedProperties.GetValueOrDefault("WriteEndpointUrl"),
        BearerToken = config.ExtendedProperties.GetValueOrDefault("WriteBearerToken"),
    };
}

public sealed record MTConnectWriteAdapterRequest(
    string Address,
    string DataType,
    object Value);
