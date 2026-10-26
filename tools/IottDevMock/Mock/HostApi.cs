using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace IottDevMock.Mock;

/// <summary>实现 IndustrialIoT.Host 被 CNC/PLC 监控与采集用到的 HTTP 契约。</summary>
public sealed partial class HostApi
{
    private readonly object _lock = new();
    private readonly List<MockDevice> _devices;
    private readonly string _scenario;
    private readonly ConcurrentDictionary<string, BatchTestResultDto> _results = new();

    public HostApi(List<MockDevice> devices, string scenario, int seed)
    {
        _devices = devices;
        _scenario = scenario;
    }

    public void Map(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok", scenario = _scenario, devices = _devices.Count }));

        app.MapGet("/api/Devices", (string? type) =>
        {
            lock (_lock)
            {
                var query = _devices.AsEnumerable();
                if (!string.IsNullOrWhiteSpace(type))
                    query = query.Where(d => string.Equals(d.Type, type, StringComparison.OrdinalIgnoreCase));
                return Results.Ok(query.Select(ToDto).ToList());
            }
        });

        app.MapGet("/api/Devices/{id}", (string id) => Find(id) is { } d ? Results.Ok(ToDto(d)) : Results.NotFound());

        app.MapPost("/api/Devices", (DeviceUpsertRequest req) =>
        {
            var d = Upsert(req, req.Id);
            return Results.Ok(ToDto(d));
        });

        app.MapPut("/api/Devices/{id}", (string id, DeviceUpsertRequest req) =>
        {
            if (Find(id) is null) return Results.NotFound();
            var d = Upsert(req, id);
            return Results.Ok(ToDto(d));
        });

        app.MapDelete("/api/Devices/{id}", (string id) =>
        {
            lock (_lock) { _devices.RemoveAll(d => d.Id == id); }
            return Results.NoContent();
        });

        app.MapPost("/api/Devices/{id}/test-connection", (string id) =>
        {
            if (Find(id) is not { } d) return Results.NotFound();
            var latency = ValueGenerator.LatencyMs(_scenario, StableHash(d.Id));
            var ok = _scenario switch
            {
                "IntermittentFault" when StableHash(d.Id) % 9 == 0 => false,
                _ => true,
            };
            return Results.Ok(new ConnectionTestResultDto
            {
                Success = ok,
                ErrorMessage = ok ? null : "Simulated connection failure",
                Latency = TimeSpan.FromMilliseconds(latency),
            });
        });

        app.MapGet("/api/addressspace/{id}", (string id, string? parentPath) =>
        {
            if (Find(id) is not { } d) return Results.NotFound();
            if (!d.Browsable)
                return Results.Json(new
                {
                    code = "ADDRESS_SPACE_BROWSING_NOT_SUPPORTED",
                    error = $"当前 {d.Protocol} 驱动未实现在线地址枚举（IottDevMock 模拟）。",
                }, statusCode: StatusCodes.Status422UnprocessableEntity);
            return Results.Ok(AddressSpaceFactory.Browse(d, parentPath));
        });

        app.MapGet("/api/addressspace/{id}/export", (string id, string? format) =>
        {
            if (Find(id) is not { } d) return Results.NotFound();
            if (!d.Browsable)
                return Results.Json(new { code = "ADDRESS_SPACE_BROWSING_NOT_SUPPORTED", error = "not supported" },
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            var sb = new System.Text.StringBuilder("Path,DisplayName,NodeType,DataType\n");
            Flatten(AddressSpaceFactory.Browse(d, null), sb);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"address_space_{id}.csv");
        });

        app.MapPost("/api/data/{id}/read", (string id, ReadTagsRequest req) =>
        {
            if (Find(id) is not { } d) return Results.NotFound();
            if (req.Tags.Count == 0) return Results.BadRequest("At least one tag is required");

            var evidence = Evidence(d, req.Verification, "application-read");
            var tags = req.Tags.Select(t => ValueGenerator.Read(d, t.Address, t.DataType, _scenario)).ToList();
            return Results.Ok(new ReadTagsResponse { DeviceId = id, Tags = tags, Evidence = evidence });
        });

        app.MapPost("/api/data/{id}/verification-directory", (string id, VerificationDirectoryRequest req) =>
        {
            if (Find(id) is not { } d) return Results.NotFound();
            var evidence = Evidence(d, req.Verification, "directory-read", d.Transfer?.Protocol ?? d.Protocol);
            var files = Enumerable.Range(0, 5)
                .Select(i => new
                {
                    Path = $"{req.Directory.TrimEnd('/')}/O{i + 1}.nc",
                    DisplayName = $"O{i + 1}.nc",
                    NodeType = "Variable",
                    DataType = "String",
                    IsReadable = true,
                    IsWritable = false,
                })
                .ToList();
            return Results.Ok(new { deviceId = id, evidence, files });
        });

        app.MapPost("/api/data/{id}/write", (string id, WriteTagsRequest req) =>
        {
            if (Find(id) is not { } d) return Results.NotFound();
            var results = req.Tags.Select(t => new WriteTagResultDto
            {
                Address = t.Address,
                Success = true,
                ErrorMessage = null,
                Timestamp = DateTimeOffset.UtcNow,
            }).ToList();
            return Results.Ok(new WriteTagsResponse
            {
                DeviceId = id,
                TotalCount = results.Count,
                SuccessCount = results.Count,
                Results = results,
            });
        });

        app.MapPost("/api/connection-verification/start", (BatchTestRequest req) =>
        {
            List<MockDevice> devices;
            lock (_lock)
            {
                devices = req.DeviceIds is { Count: > 0 }
                    ? _devices.Where(d => req.DeviceIds.Contains(d.Id, StringComparer.OrdinalIgnoreCase)).ToList()
                    : !string.IsNullOrWhiteSpace(req.DeviceType)
                        ? _devices.Where(d => string.Equals(d.Type, req.DeviceType, StringComparison.OrdinalIgnoreCase)).ToList()
                        : [];
            }
            if (devices.Count == 0) return Results.BadRequest("No devices found for the given criteria");

            var testId = Guid.NewGuid().ToString("N");
            var results = devices.Select(d =>
            {
                var ok = !(_scenario == "IntermittentFault" && StableHash(d.Id) % 9 == 0);
                return new DeviceTestResultDto
                {
                    DeviceId = d.Id,
                    Success = ok,
                    LatencyMs = ValueGenerator.LatencyMs(_scenario, StableHash(d.Id)),
                    ErrorMessage = ok ? null : "Simulated connection failure",
                };
            }).ToList();
            var success = results.Count(r => r.Success);
            var batch = new BatchTestResultDto
            {
                TestId = testId,
                TotalDevices = devices.Count,
                SuccessCount = success,
                FailureCount = devices.Count - success,
                SuccessRate = devices.Count == 0 ? 0 : Math.Round((double)success / devices.Count, 4),
                Results = results,
                DurationMs = results.Sum(r => r.LatencyMs),
            };
            _results[testId] = batch;
            return Results.Ok(batch);
        });

        app.MapGet("/api/connection-verification/{testId}", (string testId) =>
            _results.TryGetValue(testId, out var r) ? Results.Ok(r) : Results.NotFound());

        app.MapGet("/api/plc/capabilities", () => Results.Ok(PlcCapabilities));

        MapP2(app);
    }

    private static void Flatten(IReadOnlyList<AddressNodeDto> nodes, System.Text.StringBuilder sb)
    {
        foreach (var n in nodes)
        {
            sb.Append(n.Path).Append(',').Append(n.DisplayName).Append(',').Append(n.NodeType)
              .Append(',').Append(n.DataType ?? "").Append('\n');
            if (n.Children is { Count: > 0 }) Flatten(n.Children, sb);
        }
    }

    private MockDevice? Find(string id)
    {
        lock (_lock) { return _devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)); }
    }

    private MockDevice Upsert(DeviceUpsertRequest req, string? id)
    {
        lock (_lock)
        {
            var existing = id is null ? null : _devices.FirstOrDefault(d => d.Id == id);
            var device = new MockDevice
            {
                Id = id ?? existing?.Id ?? Guid.NewGuid().ToString("N"),
                Name = req.Name ?? existing?.Name ?? "mock-device",
                Type = req.Type ?? existing?.Type ?? "PLC",
                Brand = req.Brand ?? existing?.Brand ?? "通用",
                Model = req.Model ?? existing?.Model ?? "mock",
                Protocol = req.Protocol ?? existing?.Protocol ?? "Simulator",
                Host = req.Host ?? existing?.Host ?? "127.0.0.1",
                Port = req.Port ?? existing?.Port ?? 0,
                Username = req.Username ?? existing?.Username,
                ConnectTimeoutMs = req.ConnectTimeoutMs ?? existing?.ConnectTimeoutMs ?? 10000,
                ReadTimeoutMs = req.ReadTimeoutMs ?? existing?.ReadTimeoutMs ?? 5000,
                ExtendedProperties = req.ExtendedProperties ?? existing?.ExtendedProperties ?? new(),
                Transfer = req.Transfer is null
                    ? existing?.Transfer
                    : new MockTransfer
                    {
                        Protocol = req.Transfer.Protocol ?? "FTP",
                        Host = req.Transfer.Host ?? "127.0.0.1",
                        Port = req.Transfer.Port ?? 21,
                        ExtendedProperties = req.Transfer.ExtendedProperties ?? new(),
                    },
                Browsable = existing?.Browsable ?? true,
                CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
            };
            if (existing is not null) _devices.Remove(existing);
            _devices.Add(device);
            return device;
        }
    }

    private static DeviceDto ToDto(MockDevice d) => new()
    {
        Id = d.Id, Name = d.Name, Type = d.Type, Brand = d.Brand, Model = d.Model, Protocol = d.Protocol,
        Status = "Offline", Host = d.Host, Port = d.Port, Username = d.Username,
        ConnectTimeoutMs = d.ConnectTimeoutMs, ReadTimeoutMs = d.ReadTimeoutMs,
        ExtendedProperties = d.ExtendedProperties, CreatedAt = d.CreatedAt, LastSeenAt = null,
        Transfer = d.Transfer is null ? null : new TransferDto
        {
            Protocol = d.Transfer.Protocol, Host = d.Transfer.Host, Port = d.Transfer.Port,
            ConnectTimeoutMs = 10000, ReadTimeoutMs = 5000, ExtendedProperties = d.Transfer.ExtendedProperties,
        },
    };

    private static VerificationEvidenceDto Evidence(MockDevice d, VerificationExpectationDto? expected, string operation,
        string? protocol = null) => new()
    {
        Version = 1,
        Operation = operation,
        Protocol = protocol ?? d.Protocol,
        Simulated = false,
        ConfigurationMatched = true,
        OperationAttempted = true,
        // 回显网关下发的期望指纹，使网关的指纹比对通过（仅调试用）。
        ConfigurationFingerprint = !string.IsNullOrWhiteSpace(expected?.ConfigurationFingerprint)
            ? expected!.ConfigurationFingerprint
            : NewHex(),
        Revision = !string.IsNullOrWhiteSpace(expected?.Revision) && IsHex64(expected!.Revision!)
            ? expected.Revision!
            : NewHex(),
        // 网关要求验证读取的 evidence.code == "completed" 才算“已执行的操作”。
        Code = expected is null ? "" : "completed",
    };

    private static bool IsHex64(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string NewHex() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in text) hash = (hash ^ c) * 16777619;
            return hash & 0x7fffffff;
        }
    }

    private static readonly object[] PlcCapabilities =
    [
        new
        {
            brand = "汇川",
            models = new[] { "H3U", "H5U", "AM600" },
            protocols = new[] { "Inovance", "InovanceSerial", "InovanceSerialOverTcp", "ModbusTCP", "ModbusRTU" },
            requiredProperties = new Dictionary<string, string[]>
            {
                ["Inovance"] = ["Series"],
                ["InovanceSerial"] = ["Series", "PortName"],
                ["InovanceSerialOverTcp"] = ["Series"],
                ["ModbusRTU"] = ["PortName"],
            },
            addressExamples = new[] { "M0", "D100", "HR100", "C0" },
        },
        new
        {
            brand = "欧姆龙",
            models = new[] { "CJ2M", "CP1W", "CP1H" },
            protocols = new[] { "FINS", "OmronHostLink" },
            requiredProperties = new Dictionary<string, string[]>(),
            addressExamples = new[] { "D100", "CIO100", "HR100", "A100" },
        },
        new
        {
            brand = "西门子",
            models = new[] { "S7-1200", "S7-1500", "S7-300", "S7-400" },
            protocols = new[] { "SiemensS7", "OpcUa" },
            requiredProperties = new Dictionary<string, string[]> { ["SiemensS7"] = ["可选 Rack/Slot/StringLength"] },
            addressExamples = new[] { "M100", "M100.0", "MW100", "DB1.100" },
        },
    ];
}

public record DeviceUpsertRequest
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
    public string? Brand { get; init; }
    public string? Model { get; init; }
    public string? Protocol { get; init; }
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? Username { get; init; }
    public int? ConnectTimeoutMs { get; init; }
    public int? ReadTimeoutMs { get; init; }
    public Dictionary<string, string>? ExtendedProperties { get; init; }
    public TransferUpsert? Transfer { get; init; }
}

public record TransferUpsert
{
    public string? Protocol { get; init; }
    public string? Host { get; init; }
    public int? Port { get; init; }
    public Dictionary<string, string>? ExtendedProperties { get; init; }
}

public record VerificationDirectoryRequest
{
    public string Directory { get; init; } = "";
    public VerificationExpectationDto? Verification { get; init; }
}
