using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace IottDevMock.Mock;

// 阶段 2：程序传输 / 采集 / 采集配置 / 批量导入 / NC-Link 诊断
public sealed partial class HostApi
{
    private readonly ConcurrentDictionary<string, List<ProgramTransferResponseDto>> _history = new();
    private readonly ConcurrentDictionary<string, byte[]> _artifacts = new();
    private readonly ConcurrentDictionary<string, BatchTransferTaskDto> _tasks = new();
    private readonly ConcurrentDictionary<string, CollectionProfileDto> _profiles = new();
    private readonly ConcurrentDictionary<string, CollectionStatusDto> _collectionTasks = new();

    private void MapP2(WebApplication app)
    {
        // ---------- 程序传输 ----------
        app.MapGet("/api/program-transfer/{deviceId}/files", (string deviceId, string? path, bool? recursive) =>
            Find(deviceId) is null ? Results.NotFound() : Results.Ok(FileList(deviceId, path)));

        app.MapGet("/api/program-transfer/{deviceId}/browse", (string deviceId, string? path) =>
            Find(deviceId) is { } d ? Results.Ok(AddressSpaceFactory.Browse(d, path)) : Results.NotFound());

        app.MapGet("/api/program-transfer/{deviceId}/capabilities", (string deviceId) =>
        {
            if (Find(deviceId) is not { } d) return Results.NotFound();
            return Results.Ok(Capability(d.Transfer?.Protocol ?? d.Protocol));
        });

        app.MapGet("/api/program-transfer/{deviceId}/history", (string deviceId) =>
            Find(deviceId) is null ? Results.NotFound() : Results.Ok(History(deviceId)));

        app.MapGet("/api/program-transfer/{transferId}/status", (string transferId) =>
        {
            var rec = _history.Values.SelectMany(x => x).FirstOrDefault(r => r.TransferId == transferId);
            return rec is null ? Results.NotFound() : Results.Ok(rec);
        });

        app.MapPost("/api/program-transfer/{deviceId}/upload", async (string deviceId, HttpRequest req) =>
        {
            if (Find(deviceId) is null) return Results.NotFound();
            var form = await req.ReadFormAsync();
            var file = form.Files.GetFile("file");
            var remotePath = form["remotePath"].ToString();
            var name = file?.FileName ?? "program.nc";
            var size = file?.Length ?? 0;
            var rec = Record(deviceId, name, remotePath, "Upload", size);
            return Results.Ok(rec);
        });

        app.MapPost("/api/program-transfer/{deviceId}/download", (string deviceId, DownloadProgramRequest body) =>
        {
            if (Find(deviceId) is null) return Results.NotFound();
            var name = Path.GetFileName(body.RemotePath.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(name)) name = "program.nc";
            var bytes = Encoding.UTF8.GetBytes(
                $"%\n(SIM NC PROGRAM {name})\nG0 X0 Y0\nM30\n% IottDevMock {DateTimeOffset.UtcNow:O}\n");
            Record(deviceId, name, body.RemotePath, "Download", bytes.Length);
            return Results.File(bytes, "application/octet-stream", name);
        });

        app.MapPost("/api/program-transfer/{deviceId}/resume", (string deviceId, ResumeUploadRequest body) =>
        {
            if (Find(deviceId) is null) return Results.NotFound();
            var source = _history.Values.SelectMany(x => x).FirstOrDefault(r => r.TransferId == body.TransferId);
            var remotePath = source?.RemotePath ?? "/Programs/resume.nc";
            var name = source?.FileName ?? "resume.nc";
            var rec = Record(deviceId, name, remotePath, "Upload", (source?.FileSize ?? 4096));
            return Results.Ok(rec);
        });

        app.MapPost("/api/program-transfer/{deviceId}/download-batch", (string deviceId, BatchDownloadRequest body) =>
        {
            if (Find(deviceId) is null) return Results.NotFound();
            if (body.Paths.Count == 0) return Results.BadRequest("At least one remote path is required");
            var task = CompleteBatchDownload(deviceId, body.Paths);
            return Results.Accepted($"/api/program-transfer/tasks/{task.TaskId}", new BatchTransferAcceptedDto
            {
                TaskId = task.TaskId, Status = task.Status,
            });
        });

        app.MapPost("/api/program-transfer/{deviceId}/upload-batch", async (string deviceId, HttpRequest req) =>
        {
            if (Find(deviceId) is null) return Results.NotFound();
            var form = await req.ReadFormAsync();
            var files = form.Files.GetFiles("files");
            if (files.Count == 0) return Results.BadRequest("At least one upload file is required");
            var remotePath = form["remotePath"].ToString();
            var task = CompleteBatchUpload(deviceId, remotePath, files.Select(f => f.FileName).ToList());
            return Results.Accepted($"/api/program-transfer/tasks/{task.TaskId}", new BatchTransferAcceptedDto
            {
                TaskId = task.TaskId, Status = task.Status,
            });
        });

        app.MapGet("/api/program-transfer/tasks/{taskId}", (string taskId) =>
            _tasks.TryGetValue(taskId, out var t) ? Results.Ok(t) : Results.NotFound());

        app.MapGet("/api/program-transfer/tasks/{taskId}/artifact", (string taskId) =>
            _artifacts.TryGetValue(taskId, out var bytes)
                ? Results.File(bytes, "application/zip", $"batch-{taskId}.zip")
                : Results.NotFound());

        // ---------- 采集 ----------
        app.MapPost("/api/collection/start", (StartCollectionRequest req) =>
        {
            var taskId = Guid.NewGuid().ToString("N");
            _collectionTasks[taskId] = new CollectionStatusDto
            {
                TaskId = taskId, DeviceId = req.DeviceId, IsRunning = true,
                LastCollectedAt = DateTimeOffset.UtcNow, TotalCollections = 1, TotalErrors = 0,
            };
            return Results.Ok(new { taskId });
        });

        app.MapPost("/api/collection/stop/{taskId}", (string taskId) =>
        {
            if (_collectionTasks.TryGetValue(taskId, out var s))
                _collectionTasks[taskId] = s with { IsRunning = false };
            return Results.NoContent();
        });

        app.MapGet("/api/collection/status", () =>
            Results.Ok(_collectionTasks.Values.ToDictionary(s => s.TaskId, s => s)));

        // ---------- 采集配置 ----------
        app.MapGet("/api/collection-config/{deviceId}", (string deviceId) =>
            Results.Ok(_profiles.Values.Where(p => p.DeviceId == deviceId).ToList()));

        app.MapGet("/api/collection-config/profile/{profileId}", (string profileId) =>
            _profiles.TryGetValue(profileId, out var p) ? Results.Ok(p) : Results.NotFound());

        app.MapPost("/api/collection-config/{deviceId}", (string deviceId, CollectionProfileCreateRequest req) =>
        {
            if (Find(deviceId) is null)
                return Results.NotFound(new { code = "DEVICE_NOT_FOUND", error = $"设备 {deviceId} 不存在，请从已注册设备中选择。" });
            var profile = new CollectionProfileDto
            {
                Id = Guid.NewGuid().ToString("N"),
                DeviceId = deviceId,
                Name = req.Name,
                IsEnabled = true,
                Groups = req.Groups,
            };
            _profiles[profile.Id] = profile;
            return Results.Created($"/api/collection-config/profile/{profile.Id}", profile);
        });

        app.MapPut("/api/collection-config/profile/{profileId}", (string profileId, CollectionProfileCreateRequest req) =>
        {
            if (!_profiles.TryGetValue(profileId, out var existing)) return Results.NotFound();
            var updated = existing with { Name = req.Name, Groups = req.Groups };
            _profiles[profileId] = updated;
            return Results.Ok(updated);
        });

        app.MapDelete("/api/collection-config/profile/{profileId}", (string profileId) =>
        {
            _profiles.TryRemove(profileId, out _);
            return Results.NoContent();
        });

        // ---------- 批量导入 ----------
        app.MapPost("/api/batch-import/tags/{deviceId}", async (string deviceId, HttpRequest req) =>
        {
            if (Find(deviceId) is null) return Results.NotFound($"Device {deviceId} not found");
            var form = await req.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0) return Results.BadRequest("File is empty");
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext is not ".csv" and not ".json") return Results.BadRequest("Only .csv and .json files are supported");

            using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync();
            var rows = CountRows(ext, text, out var errors);
            return Results.Ok(new BatchImportResultDto
            {
                TotalRows = rows,
                SuccessCount = errors.Count == 0 ? rows : 0,
                ErrorCount = errors.Count,
                Errors = errors,
            });
        });

        // ---------- NC-Link 诊断 ----------
        app.MapGet("/api/nclink/{deviceId}/probe", (string deviceId) =>
        {
            if (Find(deviceId) is not { } d) return Results.NotFound();
            if (d.Protocol != "NCLink") return Results.BadRequest($"Device protocol {d.Protocol} is not NC-Link");
            var di = NcDataItems();
            var sc = NcSampleChannels();
            return Results.Ok(new
            {
                id = deviceId, guid = d.ExtendedProperties.GetValueOrDefault("DeviceGuid", "sim-guid"),
                version = "1.0", dataItemCount = di.Count, sampleChannelCount = sc.Count,
                dataItems = di, sampleChannels = sc,
            });
        });

        app.MapGet("/api/nclink/{deviceId}/dataitems", (string deviceId) =>
        {
            if (Find(deviceId) is not { } d) return Results.NotFound();
            if (d.Protocol != "NCLink") return Results.BadRequest($"Device protocol {d.Protocol} is not NC-Link");
            return Results.Ok(NcDataItems());
        });

        app.MapGet("/api/nclink/{deviceId}/sample-channels", (string deviceId) =>
        {
            if (Find(deviceId) is not { } d) return Results.NotFound();
            if (d.Protocol != "NCLink") return Results.BadRequest($"Device protocol {d.Protocol} is not NC-Link");
            return Results.Ok(NcSampleChannels());
        });

        MapP3(app);
    }

    // ---------- 辅助 ----------

    private static int StableHashLocal(string text)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in text) hash = (hash ^ c) * 16777619;
            return hash & 0x7fffffff;
        }
    }

    private List<ProgramTransferResponseDto> History(string deviceId) => _history.GetOrAdd(deviceId, id =>
    [
        new ProgramTransferResponseDto
        {
            TransferId = Guid.NewGuid().ToString("N"), DeviceId = id, FileName = "O1001.nc",
            RemotePath = "/Programs/O1001.nc", Direction = "Upload", Status = "Completed",
            FileSize = 2048, BytesTransferred = 2048, Checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("O1001"))).ToLowerInvariant(),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30), CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-29), DurationMs = 1200,
        },
        new ProgramTransferResponseDto
        {
            TransferId = Guid.NewGuid().ToString("N"), DeviceId = id, FileName = "O1002.nc",
            RemotePath = "/Programs/O1002.nc", Direction = "Download", Status = "Completed",
            FileSize = 4096, BytesTransferred = 4096,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10), CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-9), DurationMs = 900,
        },
    ]);

    private ProgramTransferResponseDto Record(string deviceId, string fileName, string remotePath, string direction, long size)
    {
        var rec = new ProgramTransferResponseDto
        {
            TransferId = Guid.NewGuid().ToString("N"), DeviceId = deviceId, FileName = fileName,
            RemotePath = remotePath, Direction = direction, Status = "Completed",
            FileSize = size, BytesTransferred = size,
            Checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileName))).ToLowerInvariant(),
            StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow, DurationMs = 250,
        };
        History(deviceId).Add(rec);
        return rec;
    }

    private static ProgramTransferCapabilityDto Capability(string protocol)
    {
        var resume = protocol is "FTP" or "Serial";
        return new ProgramTransferCapabilityDto
        {
            Protocol = protocol,
            SupportsProgramTransfer = true,
            SupportsBrowse = true,
            SupportsResumeUpload = resume,
            SupportsFullRestartUpload = true,
            ResumeMode = resume ? "ResumeUpload" : "FullRestartUpload",
            Limitation = resume ? null : "Protocol driver cannot resume partial uploads; retry from byte 0 is supported.",
        };
    }

    private static List<ProgramFileNodeDto> FileList(string deviceId, string? path)
    {
        var now = DateTimeOffset.UtcNow;
        var normalized = string.IsNullOrWhiteSpace(path) ? "/" : "/" + path.Trim().Trim('/');
        if (normalized is "/" or "/root")
            return
            [
                new() { Path = "/Programs", Name = "Programs", NodeType = "Directory", ModifiedAt = now, CanUpload = true, HasChildren = true },
                new() { Path = "/Macros", Name = "Macros", NodeType = "Directory", ModifiedAt = now, CanUpload = true, HasChildren = true },
                new() { Path = "/O0001.nc", Name = "O0001.nc", NodeType = "File", SizeBytes = 2048, ModifiedAt = now, CanDownload = true },
            ];
        if (normalized == "/Programs")
            return Enumerable.Range(1000, 5).Select(i => new ProgramFileNodeDto
            {
                Path = $"/Programs/O{i}.nc", Name = $"O{i}.nc", NodeType = "File",
                SizeBytes = 1024 + StableHashLocal(deviceId + i) % 4096, ModifiedAt = now, CanDownload = true,
            }).ToList();
        if (normalized == "/Macros")
            return Enumerable.Range(1, 3).Select(i => new ProgramFileNodeDto
            {
                Path = $"/Macros/M{i}.nc", Name = $"M{i}.nc", NodeType = "File",
                SizeBytes = 512 + Stabilize(i), ModifiedAt = now, CanDownload = true,
            }).ToList();
        return [];
    }

    private static int Stabilize(int i) => (i * 137) % 2048;

    private BatchTransferTaskDto CompleteBatchDownload(string deviceId, IReadOnlyList<string> paths)
    {
        var taskId = Guid.NewGuid().ToString("N");
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var p in paths)
            {
                var entry = zip.CreateEntry(Path.GetFileName(p.Replace('\\', '/')) is { Length: > 0 } n ? n : "program.nc");
                using var s = entry.Open();
                var data = Encoding.UTF8.GetBytes($"%\n(SIM {p})\nM30\n% {DateTimeOffset.UtcNow:O}\n");
                s.Write(data, 0, data.Length);
            }
        }
        _artifacts[taskId] = ms.ToArray();

        var items = paths.Select(p => new BatchTransferItemDto
        {
            FileName = Path.GetFileName(p.Replace('\\', '/')) is { Length: > 0 } n ? n : "program.nc",
            RemotePath = p, Status = "Completed", FileSize = 1024, BytesTransferred = 1024, DurationMs = 50,
        }).ToList();
        var task = new BatchTransferTaskDto
        {
            TaskId = taskId, DeviceId = deviceId, Direction = "Download", Status = "Completed",
            TotalFiles = items.Count, CompletedFiles = items.Count, FailedFiles = 0,
            TotalBytes = items.Sum(i => i.FileSize), BytesTransferred = items.Sum(i => i.BytesTransferred),
            Items = items, StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = 100, ThroughputBytesPerSecond = 10240, ArtifactReady = true, ArtifactFileName = $"batch-{taskId}.zip",
        };
        _tasks[taskId] = task;
        return task;
    }

    private BatchTransferTaskDto CompleteBatchUpload(string deviceId, string remotePath, IReadOnlyList<string> fileNames)
    {
        var taskId = Guid.NewGuid().ToString("N");
        var items = fileNames.Select(f => new BatchTransferItemDto
        {
            FileName = f, RemotePath = $"{remotePath.TrimEnd('/')}/{f}", Status = "Completed",
            FileSize = 2048, BytesTransferred = 2048, DurationMs = 40,
        }).ToList();
        var task = new BatchTransferTaskDto
        {
            TaskId = taskId, DeviceId = deviceId, Direction = "Upload", Status = "Completed",
            TotalFiles = items.Count, CompletedFiles = items.Count, FailedFiles = 0,
            TotalBytes = items.Sum(i => i.FileSize), BytesTransferred = items.Sum(i => i.BytesTransferred),
            Items = items, StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = 80, ThroughputBytesPerSecond = 25600, ArtifactReady = false,
        };
        _tasks[taskId] = task;
        return task;
    }

    private static int CountRows(string ext, string text, out List<string> errors)
    {
        errors = [];
        if (string.IsNullOrWhiteSpace(text)) { errors.Add("No tag rows were provided"); return 0; }
        if (ext == ".json")
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
            }
            catch (JsonException ex) { errors.Add($"Invalid JSON: {ex.Message}"); return 0; }
        }
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return Math.Max(0, lines.Length - 1); // 去表头
    }

    private static List<object> NcDataItems() =>
    [
        new { id = "spindle.speed", name = "主轴转速", type = "double", settable = false, unit = "rpm", componentPath = "Spindle" },
        new { id = "spindle.load", name = "主轴负载", type = "double", settable = false, unit = "%", componentPath = "Spindle" },
        new { id = "feed.rate", name = "进给速度", type = "double", settable = false, unit = "mm/min", componentPath = "Feed" },
        new { id = "program.name", name = "程序名", type = "string", settable = false, unit = "", componentPath = "Program" },
        new { id = "program.number", name = "程序号", type = "int32", settable = false, unit = "", componentPath = "Program" },
    ];

    private static List<object> NcSampleChannels() =>
    [
        new { id = "ch-pos", name = "位置采样", sampleIntervalMs = 100, dataItemIds = new[] { "feed.rate" } },
        new { id = "ch-spindle", name = "主轴采样", sampleIntervalMs = 200, dataItemIds = new[] { "spindle.speed", "spindle.load" } },
    ];
}
