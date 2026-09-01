using System.Text.Json;

namespace MachineConnectionApi.Services;

public sealed record NCLinkDeviceIdentity(
    string DeviceId,
    string? Protocol,
    bool HasConfiguredDeviceId = false)
{
    public bool IsNCLinkApi =>
        Protocol?.Equals("NCLinkApi", StringComparison.OrdinalIgnoreCase) == true;
}

public interface INCLinkDeviceResolver
{
    Task<NCLinkDeviceIdentity> ResolveAsync(string deviceId, CancellationToken ct = default);
}

public sealed class NCLinkDeviceResolver : INCLinkDeviceResolver
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    public NCLinkDeviceResolver(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<NCLinkDeviceIdentity> ResolveAsync(string deviceId, CancellationToken ct = default)
    {
        try
        {
            var devicesPath = _configuration["IndustrialIoT:DevicesPath"] ?? "api/Devices";
            var client = _httpClientFactory.CreateClient("IndustrialIoT");
            using var response = await client.GetAsync(
                $"{devicesPath}/{Uri.EscapeDataString(deviceId)}", ct);
            if (!response.IsSuccessStatusCode)
                return new NCLinkDeviceIdentity(deviceId, null);

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var protocol = TryGetStringProperty(doc.RootElement, "protocol");
            var realDeviceId = TryGetExtendedDeviceId(doc.RootElement);
            return new NCLinkDeviceIdentity(
                realDeviceId ?? deviceId,
                protocol,
                !string.IsNullOrWhiteSpace(realDeviceId));
        }
        catch
        {
            return new NCLinkDeviceIdentity(deviceId, null);
        }
    }

    private static string? TryGetExtendedDeviceId(JsonElement root)
    {
        if (!TryGetProperty(root, "extendedProperties", out var props))
            return null;
        var value = TryGetStringProperty(props, "DeviceId");
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? TryGetStringProperty(JsonElement item, string name)
    {
        if (!TryGetProperty(item, name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static bool TryGetProperty(JsonElement item, string name, out JsonElement value)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in item.EnumerateObject())
            {
                if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
