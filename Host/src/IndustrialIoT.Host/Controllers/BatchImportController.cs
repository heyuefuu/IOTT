namespace IndustrialIoT.Host.Controllers;

using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Host.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/batch-import")]
public class BatchImportController : ControllerBase
{
    private readonly IDeviceRepository _deviceRepo;
    private readonly ICollectionProfileRepository _profileRepo;
    private readonly ILogger<BatchImportController> _logger;

    public BatchImportController(
        IDeviceRepository deviceRepo,
        ICollectionProfileRepository profileRepo,
        ILogger<BatchImportController> logger)
    {
        _deviceRepo = deviceRepo;
        _profileRepo = profileRepo;
        _logger = logger;
    }

    /// <summary>批量导入采集点位配置（CSV 格式）</summary>
    [HttpPost("tags/{deviceId}")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<BatchImportResult>> ImportTags(
        string deviceId, IFormFile file, CancellationToken ct)
    {
        var device = await _deviceRepo.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound($"Device {deviceId} not found");

        if (file.Length == 0)
            return BadRequest("File is empty");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not ".csv" and not ".json")
            return BadRequest("Only .csv and .json files are supported");

        var errors = new List<string>();
        var rows = new List<TagImportRow>();

        using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream);

        if (ext == ".csv")
        {
            rows = await ParseCsvAsync(reader, errors, ct);
        }
        else
        {
            var json = await reader.ReadToEndAsync(ct);
            try
            {
                rows = System.Text.Json.JsonSerializer.Deserialize<List<TagImportRow>>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            }
            catch (System.Text.Json.JsonException ex)
            {
                return BadRequest($"Invalid JSON: {ex.Message}");
            }
        }

        var totalRows = rows.Count;

        // Validate rows
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var lineNum = i + 2; // 1-based, +1 for header
            if (string.IsNullOrWhiteSpace(row.Address))
                errors.Add($"Row {lineNum}: Address is required");
            if (string.IsNullOrWhiteSpace(row.GroupName))
                errors.Add($"Row {lineNum}: GroupName is required");
            if (!Enum.TryParse<DataType>(row.DataType, true, out _))
                errors.Add($"Row {lineNum}: Invalid DataType '{row.DataType}'");
            if (row.IntervalMs <= 0)
                errors.Add($"Row {lineNum}: IntervalMs must be > 0");
        }

        if (errors.Count > 0)
        {
            return Ok(new BatchImportResult
            {
                TotalRows = totalRows,
                SuccessCount = 0,
                ErrorCount = errors.Count,
                Errors = errors,
            });
        }

        // Build profile from imported rows
        var profile = new CollectionProfile
        {
            DeviceId = deviceId,
            Name = $"Import_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}",
            IsEnabled = true,
        };

        var groupMap = new Dictionary<string, CollectionGroup>();
        var successCount = 0;

        foreach (var row in rows)
        {
            var dataType = Enum.Parse<DataType>(row.DataType, true);

            if (!groupMap.TryGetValue(row.GroupName, out var group))
            {
                group = new CollectionGroup
                {
                    ProfileId = profile.Id,
                    GroupName = row.GroupName,
                    IntervalMs = row.IntervalMs,
                };
                groupMap[row.GroupName] = group;
                profile.Groups.Add(group);
            }

            group.Tags.Add(new TagConfig
            {
                GroupId = group.Id,
                Address = row.Address,
                DataType = dataType,
                DisplayName = row.DisplayName,
                Unit = row.Unit,
            });
            successCount++;
        }

        await _profileRepo.AddAsync(profile, ct);

        _logger.LogInformation(
            "Batch import completed for device {DeviceId}: {Success}/{Total} tags imported",
            deviceId, successCount, totalRows);

        return Ok(new BatchImportResult
        {
            TotalRows = totalRows,
            SuccessCount = successCount,
            ErrorCount = 0,
            Errors = [],
        });
    }

    private static async Task<List<TagImportRow>> ParseCsvAsync(
        StreamReader reader, List<string> errors, CancellationToken ct)
    {
        var parsedRows = await TagImportCsvParser.ParseAsync(reader, ct);
        return parsedRows.Select(row => new TagImportRow
        {
            Address = row.Address,
            DataType = row.DataType,
            GroupName = row.GroupName,
            IntervalMs = row.IntervalMs,
            DisplayName = row.DisplayName,
            Unit = row.Unit,
        }).ToList();
    }

    private sealed class TagImportRow
    {
        public string Address { get; init; } = "";
        public string DataType { get; init; } = "";
        public string? DisplayName { get; init; }
        public string? Unit { get; init; }
        public string GroupName { get; init; } = "";
        public int IntervalMs { get; init; }
    }
}

public record BatchImportResult
{
    public int TotalRows { get; init; }
    public int SuccessCount { get; init; }
    public int ErrorCount { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
}
