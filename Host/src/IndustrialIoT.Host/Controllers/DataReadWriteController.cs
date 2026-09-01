namespace IndustrialIoT.Host.Controllers;

using System.Text.Json;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Host.Services;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/data")]
public class DataReadWriteController : ControllerBase
{
    private readonly IPooledDriverAccessor _drivers;
    private readonly IDeviceRepository _deviceRepo;
    private readonly ILogger<DataReadWriteController> _logger;

    public DataReadWriteController(
        IPooledDriverAccessor drivers,
        IDeviceRepository deviceRepo,
        ILogger<DataReadWriteController> logger)
    {
        _drivers = drivers;
        _deviceRepo = deviceRepo;
        _logger = logger;
    }

    /// <summary>从设备读取点位数据</summary>
    [HttpPost("{deviceId}/read")]
    public async Task<ActionResult<ReadTagsResponse>> ReadTags(
        string deviceId,
        [FromBody] ReadTagsRequest request,
        CancellationToken ct)
    {
        var device = await _deviceRepo.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound($"Device {deviceId} not found");

        if (request.Tags.Count == 0)
            return BadRequest("At least one tag is required");

        var tagRequests = new List<TagReadRequest>(request.Tags.Count);
        foreach (var tag in request.Tags)
        {
            if (!TryResolveReadDataType(tag.Address, tag.DataType, out var dataType, out var error))
                return BadRequest(error);

            tagRequests.Add(new TagReadRequest
            {
                Address = tag.Address,
                DataType = dataType,
            });
        }

        // Reuse the pooled long-lived connection — see PooledDriverAccessor
        var outcome = await _drivers.ExecuteAsync(
            deviceId,
            driver => driver.ReadTagsAsync(tagRequests, ct),
            ct);

        if (!outcome.Success)
            return StatusCode(502, new { error = $"Read failed: {outcome.ErrorMessage}" });

        var values = outcome.Value!;

        _logger.LogInformation(
            "Read {Count} tags from device {DeviceId}",
            values.Count, deviceId);

        return Ok(new ReadTagsResponse
        {
            DeviceId = deviceId,
            Tags = values.Select(v => new TagValueDto
            {
                Address = v.Address,
                DataType = v.DataType.ToString(),
                Value = v.Value,
                Quality = v.Quality.ToString(),
                Timestamp = v.Timestamp,
                ErrorMessage = v.ErrorMessage,
            }).ToList(),
        });
    }

    /// <summary>向设备写入点位数据</summary>
    [HttpPost("{deviceId}/write")]
    public async Task<ActionResult<WriteTagsResponse>> WriteTags(
        string deviceId,
        [FromBody] WriteTagsRequest request,
        CancellationToken ct)
    {
        var device = await _deviceRepo.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound($"Device {deviceId} not found");

        if (request.Tags.Count == 0)
            return BadRequest("At least one tag is required");

        // Validate every tag before touching the device so a bad payload cannot half-write
        var writes = new List<(string Address, DataType DataType, object? Value)>(request.Tags.Count);
        foreach (var tag in request.Tags)
        {
            if (!TryParseDataType(tag.DataType, out var dataType))
                return BadRequest(CreateInvalidDataTypeMessage(tag.DataType));

            writes.Add((tag.Address, dataType, NormalizeWriteValue(dataType, tag.Value)));
        }

        // Reuse the pooled long-lived connection — see PooledDriverAccessor
        var outcome = await _drivers.ExecuteAsync(
            deviceId,
            async driver =>
            {
                var list = new List<WriteTagResultDto>(writes.Count);
                foreach (var (address, dataType, value) in writes)
                {
                    var result = await driver.WriteTagAsync(address, dataType, value!, ct);
                    list.Add(new WriteTagResultDto
                    {
                        Address = address,
                        Success = result.Success,
                        ErrorMessage = result.ErrorMessage,
                        Timestamp = result.Timestamp,
                    });
                }
                return list;
            },
            ct);

        if (!outcome.Success)
            return StatusCode(502, new { error = $"Write failed: {outcome.ErrorMessage}" });

        var results = outcome.Value!;

        var successCount = results.Count(r => r.Success);
        _logger.LogInformation(
            "Write {Success}/{Total} tags to device {DeviceId}",
            successCount, results.Count, deviceId);

        return Ok(new WriteTagsResponse
        {
            DeviceId = deviceId,
            TotalCount = results.Count,
            SuccessCount = successCount,
            Results = results,
        });
    }

    private static object NormalizeWriteValue(DataType dataType, object value)
    {
        if (value is JsonElement element && dataType != DataType.ByteArray)
        {
            return dataType switch
            {
                DataType.Bool => element.GetBoolean(),
                DataType.Int16 => element.GetInt16(),
                DataType.Int32 => element.GetInt32(),
                DataType.Int64 => element.GetInt64(),
                DataType.UInt16 => element.GetUInt16(),
                DataType.UInt32 => element.GetUInt32(),
                DataType.Float => element.GetSingle(),
                DataType.Double => element.GetDouble(),
                DataType.String => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString(),
                _ => value,
            };
        }

        if (dataType != DataType.ByteArray) return value;

        return value switch
        {
            byte[] bytes => bytes,
            string text => ParseByteArray(text),
            JsonElement elem => ParseByteArray(elem),
            _ => value,
        };
    }

    private static byte[] ParseByteArray(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('['))
        {
            return JsonSerializer.Deserialize<byte[]>(trimmed) ?? [];
        }

        var parts = trimmed.Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var useHex = parts.Any(part => part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || part.Any(Uri.IsHexDigit) && part.Any(char.IsLetter));
        return parts.Select(part =>
        {
            var token = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part;
            return Convert.ToByte(token, useHex ? 16 : 10);
        }).ToArray();
    }

    private static byte[] ParseByteArray(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => ParseByteArray(element.GetString() ?? string.Empty),
        JsonValueKind.Array => element.EnumerateArray().Select(item => item.GetByte()).ToArray(),
        _ => throw new InvalidOperationException($"Unsupported ByteArray payload kind: {element.ValueKind}"),
    };

    public static bool TryResolveReadDataType(string address, string? input, out DataType dataType, out string? error)
    {
        if (!string.IsNullOrWhiteSpace(input))
        {
            var ok = TryParseDataType(input, out dataType);
            error = ok ? null : CreateInvalidDataTypeMessage(input);
            return ok;
        }

        dataType = InferDataType(address);
        error = null;
        return true;
    }

    private static bool TryParseDataType(string input, out DataType dataType)
    {
        var normalized = input.Trim();
        if (normalized.Equals("int", StringComparison.OrdinalIgnoreCase))
            normalized = nameof(DataType.Int32);
        else if (normalized.Equals("uint", StringComparison.OrdinalIgnoreCase))
            normalized = nameof(DataType.UInt32);
        else if (normalized.Equals("real", StringComparison.OrdinalIgnoreCase))
            normalized = nameof(DataType.Float);

        return Enum.TryParse(normalized, ignoreCase: true, out dataType);
    }

    private static DataType InferDataType(string address)
    {
        var normalized = address.Trim().TrimStart('/');
        return normalized.ToLowerInvariant() switch
        {
            "static.axiscount" => DataType.Int32,
            "static.spindlecount" => DataType.Int32,
            "static.pathcount" => DataType.Int32,
            "static.systemtype" => DataType.String,
            var value when value.StartsWith("macro:") => DataType.Double,
            _ => DataType.String
        };
    }

    private static string CreateInvalidDataTypeMessage(string value) =>
        $"Invalid dataType '{value}'. Supported values: {string.Join(", ", Enum.GetNames<DataType>())}. Use String for units/text.";
}

public record ReadTagsRequest
{
    public required IReadOnlyList<TagReadRequestDto> Tags { get; init; }
}

public record TagReadRequestDto
{
    public required string Address { get; init; }
    public string? DataType { get; init; }
}

public record ReadTagsResponse
{
    public required string DeviceId { get; init; }
    public required IReadOnlyList<TagValueDto> Tags { get; init; }
}

public record TagValueDto
{
    public required string Address { get; init; }
    public required string DataType { get; init; }
    public required object Value { get; init; }
    public required string Quality { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string? ErrorMessage { get; init; }
}

public record WriteTagsRequest
{
    public required IReadOnlyList<WriteTagRequest> Tags { get; init; }
}

public record WriteTagRequest
{
    public required string Address { get; init; }
    public required string DataType { get; init; }
    public required object Value { get; init; }
}

public record WriteTagResultDto
{
    public required string Address { get; init; }
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public record WriteTagsResponse
{
    public required string DeviceId { get; init; }
    public int TotalCount { get; init; }
    public int SuccessCount { get; init; }
    public required IReadOnlyList<WriteTagResultDto> Results { get; init; }
}
