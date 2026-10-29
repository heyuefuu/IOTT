namespace MachineConnectionApi.Models;

public sealed record SystemLogPage(IReadOnlyList<SystemLogDto> Items, int Total, int Page, int PageSize);

public sealed record SystemLogRetention(int ActiveLimit, int ArchiveRetentionDays, string TimeZone = "UTC");
