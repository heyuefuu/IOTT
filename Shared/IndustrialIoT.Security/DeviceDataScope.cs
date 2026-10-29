namespace IndustrialIoT.Security;

public sealed record DeviceDataScope
{
    public string Mode { get; init; } = "selected";
    public IReadOnlyList<string> DeviceIds { get; init; } = [];
    public bool Allows(string? id) => !string.IsNullOrWhiteSpace(id)
        && (Mode == "all" || DeviceIds.Contains(id, StringComparer.Ordinal));

    public static DeviceDataScope None { get; } = new();
    public static DeviceDataScope All { get; } = new() { Mode = "all" };

    public static void Validate(DeviceDataScope? scope)
    {
        if (scope is null) return;
        if (scope.Mode is not ("selected" or "all") || scope.DeviceIds is null
            || scope.DeviceIds.Count > 10000 || scope.DeviceIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("数据范围应为 all 或 selected，设备列表应包含有效设备 ID。");
    }
}

public sealed record HostAccessGrant(string UserId, string Role, IReadOnlyList<string> Permissions,
    DeviceDataScope Scope, DateTimeOffset ExpiresAt)
{
    public bool HasPermission(string permission) => Role == "admin"
        || Permissions.Contains("all") || Permissions.Contains(permission);
}
