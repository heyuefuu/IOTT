namespace IndustrialIoT.Host.Controllers;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Host.Services;
using IndustrialIoT.Infrastructure.BackgroundServices;
using IndustrialIoT.Protocols.HuazhongRobot;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/batch-import")]
public class BatchImportController : ControllerBase
{
    private readonly IDeviceRepository _deviceRepo;
    private readonly ICollectionProfileRepository _profileRepo;
    private readonly ICollectionImportRepository _importRepo;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ImportLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<BatchImportController> _logger;
    private readonly IDeviceConnectionPool _pool;
    private readonly HuazhongRobotAddressSpace _defaultAddressSpace;
    private static readonly JsonSerializerOptions AddressMapJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public BatchImportController(
        IDeviceRepository deviceRepo,
        ICollectionProfileRepository profileRepo,
        ILogger<BatchImportController> logger,
        IDeviceConnectionPool pool,
        HuazhongRobotAddressSpace defaultAddressSpace,
        ICollectionImportRepository importRepo)
    {
        _deviceRepo = deviceRepo;
        _profileRepo = profileRepo;
        _importRepo = importRepo;
        _logger = logger;
        _pool = pool;
        _defaultAddressSpace = defaultAddressSpace;
    }

    /// <summary>批量导入采集点位配置（CSV 格式）</summary>
    [HttpPost("tags/{deviceId}")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<BatchImportResult>> ImportTags(
        string deviceId, IFormFile file, CancellationToken ct)
    {
        var importLock = ImportLocks.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));
        await importLock.WaitAsync(ct);
        try { return await ImportTagsCoreAsync(deviceId, file, ct); }
        finally { importLock.Release(); }
    }

    private async Task<ActionResult<BatchImportResult>> ImportTagsCoreAsync(
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
            try { rows = await ParseCsvAsync(reader, errors, ct); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or Microsoft.VisualBasic.FileIO.MalformedLineException)
            {
                return BadRequest($"Invalid CSV: {ex.Message}");
            }
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
        if (totalRows == 0) errors.Add("No tag rows were provided");

        // Validate rows
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var lineNum = i + 2; // 1-based, +1 for header
            if (row is null)
            {
                errors.Add($"Row {lineNum}: Tag must be an object");
                continue;
            }
            if (string.IsNullOrWhiteSpace(row.Address))
                errors.Add($"Row {lineNum}: Address is required");
            if (string.IsNullOrWhiteSpace(row.GroupName))
                errors.Add($"Row {lineNum}: GroupName is required");
            if (!Enum.TryParse<DataType>(row.DataType, true, out var parsedType) || !Enum.IsDefined(parsedType))
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

        var connectionConfig = device.ConnectionConfig;
        if (device.Protocol == ProtocolType.HuazhongRobot)
        {
            var duplicatePaths = rows
                .Where(row => !string.IsNullOrWhiteSpace(row.Address))
                .GroupBy(row => row.Address.Trim(), StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() > 1);
            if (duplicatePaths)
            {
                return Ok(new BatchImportResult
                {
                    TotalRows = totalRows,
                    SuccessCount = 0,
                    ErrorCount = 1,
                    Errors = ["Duplicate robot address paths are not allowed"],
                });
            }

            string addressMap;
            try { addressMap = BuildRobotAddressMap(device, rows); }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                return Ok(new BatchImportResult
                {
                    TotalRows = totalRows, SuccessCount = 0, ErrorCount = 1,
                    Errors = [ex.Message],
                });
            }
            connectionConfig = device.ConnectionConfig with
            {
                ExtendedProperties = new Dictionary<string, string>(device.ConnectionConfig.ExtendedProperties)
                {
                    ["AddressMap"] = addressMap,
                },
            };
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
                Address = row.Address.Trim(),
                DataType = dataType,
                DisplayName = row.DisplayName,
                Unit = row.Unit,
            });
            successCount++;
        }

        if (device.Protocol == ProtocolType.HuazhongRobot)
        {
            await _importRepo.SaveAsync(device, connectionConfig, profile, ct);
            await _pool.ReleaseAsync(deviceId, CancellationToken.None);
        }
        else await _profileRepo.AddAsync(profile, ct);

        _logger.LogInformation(
            "Batch import completed for device {DeviceId}: {Success}/{Total} tags imported",
            deviceId, successCount, totalRows);

        return Ok(new BatchImportResult
        {
            TotalRows = totalRows,
            SuccessCount = successCount,
            ErrorCount = 0,
            Errors = [],
            AddressMap = device.Protocol == ProtocolType.HuazhongRobot
                ? connectionConfig.ExtendedProperties.GetValueOrDefault("AddressMap") : null,
        });
    }

    private string BuildRobotAddressMap(Device device, List<TagImportRow> rows)
    {
        var existingJson = device.ConnectionConfig.ExtendedProperties.GetValueOrDefault("AddressMap");
        var existing = string.IsNullOrWhiteSpace(existingJson)
            ? _defaultAddressSpace
            : new HuazhongRobotAddressSpace(JsonSerializer.Deserialize<List<HuazhongRobotAddressSpace.Node>>(
                existingJson, AddressMapJsonOptions) ?? throw new ArgumentException("AddressMap must be a JSON array."));
        var nodes = existing.All.ToDictionary(node => node.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var path = row.Address.Trim();
            nodes.TryGetValue(path, out var previous);
            var dataType = Enum.Parse<DataType>(row.DataType, true);
            if (previous is not null && previous.DataType != dataType)
                throw new ArgumentException($"Address '{path}' is configured as {previous.DataType}, not {dataType}.");
            var modbusAddress = string.IsNullOrWhiteSpace(row.ModbusAddress)
                ? previous?.ModbusAddress ?? path : row.ModbusAddress.Trim();
            nodes[path] = new(path,
                string.IsNullOrWhiteSpace(row.DisplayName) ? previous?.DisplayName ?? path : row.DisplayName.Trim(),
                modbusAddress, dataType, row.IsWritable ?? previous?.IsWritable ?? false);
        }
        var validated = new HuazhongRobotAddressSpace(nodes.Values);
        return JsonSerializer.Serialize(validated.All, AddressMapJsonOptions);
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
            ModbusAddress = row.ModbusAddress,
            IsWritable = row.IsWritable,
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
        public string? ModbusAddress { get; init; }
        public bool? IsWritable { get; init; }
    }
}

public record BatchImportResult
{
    public string? AddressMap { get; init; }
    public int TotalRows { get; init; }
    public int SuccessCount { get; init; }
    public int ErrorCount { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
}
