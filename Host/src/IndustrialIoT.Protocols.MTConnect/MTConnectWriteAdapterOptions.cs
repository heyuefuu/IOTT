namespace IndustrialIoT.Protocols.MTConnect;

using IndustrialIoT.Domain.ValueObjects;

public sealed record MTConnectWriteAdapterOptions
{
    public string? EndpointUrl { get; init; }
    public string? BearerToken { get; init; }
    public IReadOnlySet<string> Addresses { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public bool IsConfigured => !string.IsNullOrWhiteSpace(EndpointUrl) && Addresses.Count > 0;
    public bool CanWrite(string address) => IsConfigured && Addresses.Contains(address);

    public static MTConnectWriteAdapterOptions From(DeviceConnectionConfig config)
    {
        var endpoint = config.ExtendedProperties.GetValueOrDefault("WriteEndpointUrl")?.Trim();
        var addresses = (config.ExtendedProperties.GetValueOrDefault("WriteAddresses") ?? "")
            .Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("WriteEndpointUrl must be an absolute HTTP(S) URL.");
            if (addresses.Count == 0)
                throw new ArgumentException("WriteAddresses must list the vendor adapter's writable DataItem IDs.");
        }
        else if (addresses.Count > 0)
            throw new ArgumentException("WriteAddresses requires WriteEndpointUrl.");
        return new()
        {
            EndpointUrl = endpoint,
            BearerToken = config.ExtendedProperties.GetValueOrDefault("WriteBearerToken"),
            Addresses = addresses,
        };
    }
}

public sealed record MTConnectWriteAdapterRequest(
    string Address,
    string DataType,
    object Value);
