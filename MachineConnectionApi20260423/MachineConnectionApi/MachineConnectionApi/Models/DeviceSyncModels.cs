namespace MachineConnectionApi.Models;

public sealed record UpstreamSyncResult(bool Success, string Action, string? Error)
{
    public static UpstreamSyncResult Ok(string action) => new(true, action, null);
    public static UpstreamSyncResult Fail(string action, string error) => new(false, action, error);
}

public sealed record UpstreamSyncError(string DeviceId, string Name, string Error);

public sealed record UpstreamSyncReport
{
    public int Total { get; init; }
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<UpstreamSyncError> Errors { get; init; } = [];
}
