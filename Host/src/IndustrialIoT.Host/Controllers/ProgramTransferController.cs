namespace IndustrialIoT.Host.Controllers;

using System.Security.Cryptography;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Host.Services;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/program-transfer")]
public class ProgramTransferController : ControllerBase
{
    private const string TransferDeviceIdKey = "transferDeviceId";
    private readonly IProtocolDriverFactory _factory;
    private readonly IDeviceRepository _deviceRepo;
    private readonly INCProgramRepository _programRepo;
    private readonly IProgramTransferFileBrowserService _fileBrowserService;
    private readonly IBatchProgramTransferTaskService _batchTaskService;
    private readonly ILogger<ProgramTransferController> _logger;

    public ProgramTransferController(
        IProtocolDriverFactory factory,
        IDeviceRepository deviceRepo,
        INCProgramRepository programRepo,
        IProgramTransferFileBrowserService fileBrowserService,
        IBatchProgramTransferTaskService batchTaskService,
        ILogger<ProgramTransferController> logger)
    {
        _factory = factory;
        _deviceRepo = deviceRepo;
        _programRepo = programRepo;
        _fileBrowserService = fileBrowserService;
        _batchTaskService = batchTaskService;
        _logger = logger;
    }

    /// <summary>上传 NC 程序到设备</summary>
    [HttpPost("{deviceId}/upload")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<ProgramTransferResponse>> Upload(
        string deviceId,
        IFormFile file,
        [FromForm] string remotePath,
        CancellationToken ct)
    {
        try
        {
            return await UploadCore(deviceId, file, remotePath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NC program upload request failed for device {DeviceId}", deviceId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private async Task<ActionResult<ProgramTransferResponse>> UploadCore(
        string deviceId,
        IFormFile file,
        string remotePath,
        CancellationToken ct)
    {
        var device = await _deviceRepo.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound($"Device {deviceId} not found");

        if (file.Length == 0)
            return BadRequest("File is empty");

        // Save uploaded file to temp location for potential resume
        var tempDir = Path.Combine(Path.GetTempPath(), "IndustrialIoT", "uploads");
        Directory.CreateDirectory(tempDir);
        var localFilePath = Path.Combine(tempDir, $"{Guid.NewGuid():N}_{file.FileName}");
        await using (var fs = new FileStream(localFilePath, FileMode.Create))
        {
            await file.CopyToAsync(fs, ct);
        }

        var record = new NCProgram
        {
            DeviceId = deviceId,
            FileName = file.FileName,
            RemotePath = remotePath,
            LocalFilePath = localFilePath,
            FileSize = file.Length,
            Direction = TransferDirection.Upload,
            Status = TransferStatus.Pending,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await _programRepo.AddAsync(record, ct);

        var resolution = await ResolveConnectedDriverAsync(device, ct);
        if (resolution.ErrorResult is not null)
            return resolution.ErrorResult;

        await using var driver = resolution.Driver!;
        if (driver is not INCProgramTransfer transferDriver)
            return BadRequest("Device driver does not support NC program transfer");

        record.Status = TransferStatus.InProgress;
        await _programRepo.UpdateAsync(record, ct);

        await using var stream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read);

        // Compute checksum
        using var sha = SHA256.Create();
        var hashBytes = await sha.ComputeHashAsync(stream, ct);
        record.Checksum = Convert.ToHexString(hashBytes);
        stream.Position = 0;

        var metadata = new NCProgramMetadata
        {
            FileName = file.FileName,
            RemotePath = remotePath,
            FileSize = file.Length,
        };

        try
        {
            var result = await transferDriver.UploadProgramAsync(stream, metadata, ct: ct);

            record.Status = result.Success ? TransferStatus.Completed : TransferStatus.Failed;
            record.BytesTransferred = result.BytesTransferred;
            record.ProtocolTransferId = result.TransferId;
            record.CompletedAt = DateTimeOffset.UtcNow;
            record.ErrorMessage = result.ErrorMessage;
            await _programRepo.UpdateAsync(record, ct);

            _logger.LogInformation(
                "NC program upload {Status} for device {DeviceId}: {FileName} ({Bytes} bytes)",
                record.Status, deviceId, file.FileName, result.BytesTransferred);

            var response = ToResponse(record, result.Duration);
            return result.Success ? Ok(response) : StatusCode(500, response);
        }
        catch (Exception ex)
        {
            record.Status = TransferStatus.Failed;
            record.CompletedAt = DateTimeOffset.UtcNow;
            record.ErrorMessage = ex.Message;
            await _programRepo.UpdateAsync(record, ct);

            _logger.LogError(ex, "NC program upload failed for device {DeviceId}", deviceId);
            return StatusCode(500, ToResponse(record, null));
        }
    }

    /// <summary>从设备下载 NC 程序</summary>
    [HttpPost("{deviceId}/download")]
    public async Task<IActionResult> Download(
        string deviceId,
        [FromBody] DownloadProgramRequest request,
        CancellationToken ct)
    {
        var device = await _deviceRepo.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound($"Device {deviceId} not found");

        var record = new NCProgram
        {
            DeviceId = deviceId,
            FileName = Path.GetFileName(request.RemotePath),
            RemotePath = request.RemotePath,
            Direction = TransferDirection.Download,
            Status = TransferStatus.Pending,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await _programRepo.AddAsync(record, ct);

        var resolution = await ResolveConnectedDriverAsync(device, ct);
        if (resolution.ErrorResult is not null)
            return resolution.ErrorResult;

        await using var driver = resolution.Driver!;
        if (driver is not INCProgramTransfer transferDriver)
            return BadRequest("Device driver does not support NC program transfer");

        record.Status = TransferStatus.InProgress;
        await _programRepo.UpdateAsync(record, ct);

        var memoryStream = new MemoryStream();

        try
        {
            var result = await transferDriver.DownloadProgramAsync(
                request.RemotePath, memoryStream, ct: ct);

            record.Status = result.Success ? TransferStatus.Completed : TransferStatus.Failed;
            record.BytesTransferred = result.BytesTransferred;
            record.FileSize = memoryStream.Length;
            record.Checksum = result.Checksum;
            record.CompletedAt = DateTimeOffset.UtcNow;
            record.ErrorMessage = result.ErrorMessage;
            await _programRepo.UpdateAsync(record, ct);

            if (!result.Success)
                return StatusCode(500, ToResponse(record, result.Duration));

            memoryStream.Position = 0;
            return File(memoryStream, "application/octet-stream", record.FileName);
        }
        catch (Exception ex)
        {
            record.Status = TransferStatus.Failed;
            record.CompletedAt = DateTimeOffset.UtcNow;
            record.ErrorMessage = ex.Message;
            await _programRepo.UpdateAsync(record, ct);
            await memoryStream.DisposeAsync();

            _logger.LogError(ex, "NC program download failed for device {DeviceId}", deviceId);
            return StatusCode(500, ToResponse(record, null));
        }
    }

    /// <summary>获取设备的传输历史</summary>
    [HttpGet("{deviceId}/history")]
    public async Task<ActionResult<IReadOnlyList<ProgramTransferResponse>>> GetHistory(
        string deviceId, CancellationToken ct)
    {
        var device = await _deviceRepo.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound($"Device {deviceId} not found");

        var records = await _programRepo.GetByDeviceIdAsync(deviceId, ct);
        var responses = records.Select(r => ToResponse(r, ComputeDuration(r))).ToList();
        return Ok(responses);
    }

    /// <summary>获取传输记录状态</summary>
    [HttpGet("{transferId}/status")]
    public async Task<ActionResult<ProgramTransferResponse>> GetStatus(
        string transferId, CancellationToken ct)
    {
        var record = await _programRepo.GetByIdAsync(transferId, ct);
        if (record is null) return NotFound($"Transfer {transferId} not found");

        return Ok(ToResponse(record, ComputeDuration(record)));
    }

    /// <summary>断点续传上传 NC 程序</summary>
    [HttpPost("{deviceId}/resume")]
    public async Task<ActionResult<ProgramTransferResponse>> ResumeUpload(
        string deviceId,
        [FromBody] ResumeUploadRequest request,
        CancellationToken ct)
    {
        var device = await _deviceRepo.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound($"Device {deviceId} not found");

        var record = await _programRepo.GetByIdAsync(request.TransferId, ct);
        if (record is null) return NotFound($"Transfer {request.TransferId} not found");

        if (record.DeviceId != deviceId)
            return BadRequest("Transfer does not belong to this device");

        if (record.Status is not (TransferStatus.Failed or TransferStatus.Paused))
            return BadRequest($"Cannot resume transfer with status {record.Status}");

        var resolution = await ResolveConnectedDriverAsync(device, ct);
        if (resolution.ErrorResult is not null)
            return resolution.ErrorResult;

        await using var driver = resolution.Driver!;
        if (driver is not INCProgramTransfer transferDriver)
            return BadRequest("Device driver does not support NC program transfer");

        if (!transferDriver.SupportsResume)
        {
            var capability = ProgramTransferCapability.FromDriver(resolution.Protocol, driver);
            return BadRequest(new
            {
                error = "Device driver does not support partial resume upload.",
                capability,
                nextAction = "Retry upload from byte 0 using /api/program-transfer/{deviceId}/upload.",
            });
        }

        // Use the locally saved file (not RemotePath which is the device-side path)
        var localFilePath = record.LocalFilePath;
        if (string.IsNullOrEmpty(localFilePath) || !System.IO.File.Exists(localFilePath))
            return BadRequest($"Local source file not found. The original upload file may have been cleaned up.");

        // Use the protocol-level transfer ID (not the DB record ID)
        var protocolTransferId = record.ProtocolTransferId ?? record.Id;

        // Default offset to BytesTransferred if not explicitly provided
        var offset = request.Offset > 0 ? request.Offset : record.BytesTransferred;

        await using var stream = System.IO.File.OpenRead(localFilePath);
        stream.Position = offset;

        record.Status = TransferStatus.InProgress;
        await _programRepo.UpdateAsync(record, ct);

        try
        {
            var resolvedRemotePath = ResolveResumeRemotePath(resolution.Protocol, record.RemotePath, record.FileName);
            var result = await transferDriver.ResumeUploadAsync(
                protocolTransferId, resolvedRemotePath, stream, offset, ct: ct);

            record.Status = result.Success ? TransferStatus.Completed : TransferStatus.Failed;
            record.BytesTransferred = result.BytesTransferred;
            record.CompletedAt = DateTimeOffset.UtcNow;
            record.ErrorMessage = result.ErrorMessage;
            await _programRepo.UpdateAsync(record, ct);

            _logger.LogInformation(
                "NC program resume upload {Status} for device {DeviceId}: offset={Offset}, bytes={Bytes}",
                record.Status, deviceId, request.Offset, result.BytesTransferred);

            var response = ToResponse(record, result.Duration);
            return result.Success ? Ok(response) : StatusCode(500, response);
        }
        catch (Exception ex)
        {
            record.Status = TransferStatus.Failed;
            record.CompletedAt = DateTimeOffset.UtcNow;
            record.ErrorMessage = ex.Message;
            await _programRepo.UpdateAsync(record, ct);

            _logger.LogError(ex, "NC program resume upload failed for device {DeviceId}", deviceId);
            return StatusCode(500, ToResponse(record, null));
        }
    }

    /// <summary>浏览设备程序目录</summary>
    [HttpGet("{deviceId}/browse")]
    public async Task<ActionResult<IReadOnlyList<AddressNode>>> BrowseDirectory(
        string deviceId, [FromQuery] string? path = null, CancellationToken ct = default)
    {
        var device = await _deviceRepo.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound($"Device {deviceId} not found");

        var resolution = await ResolveConnectedDriverAsync(device, ct);
        if (resolution.ErrorResult is not null)
            return resolution.ErrorResult;

        await using var driver = resolution.Driver!;
        if (device.Protocol == ProtocolType.NCLink &&
            driver is IAddressSpaceBrowser fileBrowser &&
            driver is IProgramFileBrowser)
        {
            var fileNodes = await _fileBrowserService.BrowseAsync(fileBrowser, path, ct: ct);
            return Ok(fileNodes.Select(MapBrowseNode).ToArray());
        }

        if (driver is not IAddressSpaceBrowser browser)
            return BadRequest("Device driver does not support directory browsing");

        var nodes = await browser.BrowseAsync(path, ct);
        return Ok(nodes);
    }

    [HttpGet("{deviceId}/files")]
    public async Task<ActionResult<IReadOnlyList<ProgramFileNodeDto>>> GetFiles(
        string deviceId,
        [FromQuery] string? path = null,
        [FromQuery] bool? recursive = null,
        CancellationToken ct = default)
    {
        var device = await _deviceRepo.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound($"Device {deviceId} not found");

        var resolution = await ResolveConnectedDriverAsync(device, ct);
        if (resolution.ErrorResult is not null) return resolution.ErrorResult;

        await using var driver = resolution.Driver!;
        if (driver is not IAddressSpaceBrowser browser)
            return BadRequest("Device driver does not support directory browsing");

        // 前端显式传 recursive 时优先；未传则沿用协议默认（FOCAS/FTP/SMB/NFS 递归）
        var useRecursive = recursive ?? ShouldBrowseFilesRecursively(resolution.Protocol);
        var nodes = await _fileBrowserService.BrowseAsync(
            browser,
            path,
            recursive: useRecursive,
            ct: ct);
        return Ok(nodes);
    }

    [HttpGet("{deviceId}/capabilities")]
    public async Task<ActionResult<ProgramTransferCapability>> GetCapabilities(
        string deviceId, CancellationToken ct = default)
    {
        var device = await _deviceRepo.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound($"Device {deviceId} not found");

        var target = await ResolveTransferTargetDeviceAsync(device, ct);
        if (target is null)
        {
            var transferDeviceId = device.ConnectionConfig.ExtendedProperties[TransferDeviceIdKey];
            return BadRequest($"Transfer device {transferDeviceId} not found for device {device.Id}");
        }

        var protocol = device.ConnectionConfig.Transfer?.Protocol ?? target.Protocol;
        await using var driver = _factory.Create(protocol, target.Brand, target.Model);
        return Ok(ProgramTransferCapability.FromDriver(protocol, driver));
    }

    [HttpPost("{deviceId}/download-batch")]
    public async Task<ActionResult<BatchTransferAcceptedResponse>> DownloadBatch(
        string deviceId, [FromBody] BatchDownloadRequest request, CancellationToken ct)
    {
        var device = await _deviceRepo.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound($"Device {deviceId} not found");
        if (request.Paths.Count == 0) return BadRequest("At least one remote path is required");
        var response = await _batchTaskService.QueueDownloadAsync(deviceId, request.Paths, ct);
        return Accepted($"/api/program-transfer/tasks/{response.TaskId}", response);
    }

    [HttpPost("{deviceId}/upload-batch")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<BatchTransferAcceptedResponse>> UploadBatch(
        string deviceId, [FromForm] string remotePath, [FromForm] List<IFormFile> files, CancellationToken ct)
    {
        var device = await _deviceRepo.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound($"Device {deviceId} not found");
        if (string.IsNullOrWhiteSpace(remotePath)) return BadRequest("Remote path is required");
        if (files.Count == 0) return BadRequest("At least one upload file is required");
        var response = await _batchTaskService.QueueUploadAsync(deviceId, remotePath, files, ct);
        return Accepted($"/api/program-transfer/tasks/{response.TaskId}", response);
    }

    [HttpGet("tasks/{taskId}")]
    public async Task<ActionResult<BatchTransferTaskDto>> GetBatchTask(string taskId, CancellationToken ct)
    {
        var task = await _batchTaskService.GetTaskAsync(taskId, ct);
        return task is null ? NotFound($"Batch transfer task {taskId} not found") : Ok(task);
    }

    [HttpGet("tasks/{taskId}/artifact")]
    public async Task<IActionResult> DownloadBatchArtifact(string taskId, CancellationToken ct)
    {
        var artifact = await _batchTaskService.GetArtifactAsync(taskId, ct);
        if (artifact is null) return NotFound($"Artifact for task {taskId} not found");
        var stream = new FileStream(artifact.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, artifact.ContentType, artifact.FileName);
    }

    private async Task<ResolvedDriverResult> ResolveConnectedDriverAsync(Device device, CancellationToken ct)
    {
        if (device.ConnectionConfig.Transfer is not null)
        {
            return await ConnectResolvedDriverAsync(
                device.ConnectionConfig.Transfer.Protocol,
                device.Brand,
                device.Model,
                CreateTransferConfig(device.ConnectionConfig.Transfer),
                ct);
        }

        var target = await ResolveTransferTargetDeviceAsync(device, ct);
        if (target is null)
        {
            var transferDeviceId = device.ConnectionConfig.ExtendedProperties[TransferDeviceIdKey];
            return new(device.Protocol, null, BadRequest($"Transfer device {transferDeviceId} not found for device {device.Id}"));
        }

        return await ConnectResolvedDriverAsync(target.Protocol, target.Brand, target.Model, target.ConnectionConfig, ct);
    }

    private async Task<ResolvedDriverResult> ConnectResolvedDriverAsync(
        ProtocolType protocol,
        string brand,
        string model,
        DeviceConnectionConfig config,
        CancellationToken ct)
    {
        var driver = _factory.Create(protocol, brand, model);
        var connectResult = await driver.ConnectAsync(config, ct);
        if (connectResult.Success)
            return new(protocol, driver, null);

        await driver.DisposeAsync();
        return new(protocol, null, StatusCode(502, new { error = $"Connection failed: {connectResult.ErrorMessage}" }));
    }

    private async Task<Device?> ResolveTransferTargetDeviceAsync(Device device, CancellationToken ct)
    {
        if (!device.ConnectionConfig.ExtendedProperties.TryGetValue(TransferDeviceIdKey, out var transferDeviceId) ||
            string.IsNullOrWhiteSpace(transferDeviceId))
            return device;

        if (string.Equals(transferDeviceId, device.Id, StringComparison.OrdinalIgnoreCase))
            return device;

        return await _deviceRepo.GetByIdAsync(transferDeviceId, ct);
    }

    private static DeviceConnectionConfig CreateTransferConfig(TransferConnectionConfig transfer) => new()
    {
        Host = transfer.Host,
        Port = transfer.Port,
        Username = transfer.Username,
        Password = transfer.Password,
        ConnectTimeout = transfer.ConnectTimeout,
        ReadTimeout = transfer.ReadTimeout,
        ExtendedProperties = new Dictionary<string, string>(transfer.ExtendedProperties),
    };

    private static bool ShouldBrowseFilesRecursively(ProtocolType protocol) => protocol is
        ProtocolType.FOCAS or
        ProtocolType.FTP or
        ProtocolType.SMB or
        ProtocolType.NFS;

    private static AddressNode MapBrowseNode(ProgramFileNodeDto node) => new()
    {
        Path = node.Path,
        DisplayName = node.Name,
        NodeType = node.NodeType == ProgramFileNodeType.Directory ? AddressNodeType.Folder : AddressNodeType.Variable,
        DataType = node.NodeType == ProgramFileNodeType.File ? DataType.String : null,
        IsReadable = node.CanDownload,
        IsWritable = node.CanUpload,
    };

    private static TimeSpan? ComputeDuration(NCProgram record) =>
        record.CompletedAt.HasValue ? record.CompletedAt.Value - record.StartedAt : null;

    private static string ResolveResumeRemotePath(ProtocolType protocol, string remotePath, string fileName)
    {
        var separator = protocol == ProtocolType.SMB ? '\\' : '/';
        var normalized = remotePath.Replace('\\', separator).Replace('/', separator).Trim();
        if (string.IsNullOrEmpty(normalized))
            return fileName;

        var trimmed = normalized.TrimEnd(separator);
        if (trimmed.Length == 0 && normalized.StartsWith(separator))
            return $"{separator}{fileName}";
        if (trimmed.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith($"{separator}{fileName}", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        return $"{trimmed}{separator}{fileName}";
    }

    private static ProgramTransferResponse ToResponse(NCProgram record, TimeSpan? duration) => new()
    {
        TransferId = record.Id,
        DeviceId = record.DeviceId,
        FileName = record.FileName,
        RemotePath = record.RemotePath,
        Direction = record.Direction,
        Status = record.Status,
        FileSize = record.FileSize,
        BytesTransferred = record.BytesTransferred,
        Checksum = record.Checksum,
        StartedAt = record.StartedAt,
        CompletedAt = record.CompletedAt,
        DurationMs = duration?.TotalMilliseconds,
        ErrorMessage = record.ErrorMessage,
    };

    private sealed record ResolvedDriverResult(ProtocolType Protocol, IProtocolDriver? Driver, ActionResult? ErrorResult);
}

public record DownloadProgramRequest
{
    public required string RemotePath { get; init; }
}

public record ProgramTransferResponse
{
    public required string TransferId { get; init; }
    public required string DeviceId { get; init; }
    public required string FileName { get; init; }
    public required string RemotePath { get; init; }
    public TransferDirection Direction { get; init; }
    public TransferStatus Status { get; init; }
    public long FileSize { get; init; }
    public long BytesTransferred { get; init; }
    public string? Checksum { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public double? DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
}

public record ResumeUploadRequest
{
    public required string TransferId { get; init; }
    public long Offset { get; init; }
}
