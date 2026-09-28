namespace IndustrialIoT.Host.Controllers;

using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Host.Services;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.AspNetCore.Mvc;

/// <summary>Verification-only create/upload/read-back/cleanup. Normal upload endpoints are unchanged.</summary>
[ApiController]
[Route("api/program-transfer/{deviceId}/verification")]
public sealed class VerificationTransferController : ControllerBase
{
    private readonly VerificationTransferService _service;

    // Uses already registered production dependencies; MVC discovery makes both routes reachable
    // without a Program.cs change or an unregistered service dependency.
    public VerificationTransferController(IProtocolDriverFactory factory, IDeviceRepository devices,
        ILogger<VerificationTransferController> logger) => _service = new(factory, devices, logger);

    [HttpGet("snapshot")]
    public async Task<IActionResult> Snapshot(string deviceId, CancellationToken ct)
    {
        var snapshot = await _service.GetSnapshotAsync(deviceId, ct);
        return snapshot is null ? NotFound(new { error = "Device/transfer target not found." }) : Ok(snapshot);
    }

    [HttpPost("round-trip")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(VerificationTransferPolicy.MaximumRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = VerificationTransferPolicy.MaximumRequestBytes,
        MemoryBufferThreshold = 65536, ValueCountLimit = 10, ValueLengthLimit = 4096)]
    public async Task<IActionResult> RoundTrip(string deviceId, [FromForm] VerificationTransferForm form, CancellationToken ct)
    {
        if (!form.AllowWrites) return BadRequest(new { error = "Explicit verification file-write permission is required." });
        if (form.File is null || form.File.Length != form.ExpectedBytes
            || form.ExpectedBytes is <= 0 or > VerificationTransferPolicy.MaximumFileBytes
            || !VerificationTransferPolicy.IsFileName(form.RemoteName)
            || !string.Equals(form.File.FileName, form.RemoteName, StringComparison.Ordinal)
            || !VerificationTransferPolicy.IsDirectory(form.Directory)
            || Request.Form.Files.Count != 1)
            return BadRequest(new { error = "Exactly one bounded verification file with matching filename and length is required." });
        await using var source = form.File.OpenReadStream();
        var result = await _service.ExecuteAsync(deviceId, new()
        {
            AllowWrites = form.AllowWrites, Directory = form.Directory, RemoteName = form.RemoteName,
            ExpectedBytes = form.ExpectedBytes, ExpectedSha256 = form.ExpectedSha256, SnapshotToken = form.SnapshotToken,
        }, source, ct);
        return Ok(result);
    }
}

// Wrapping IFormFile in a form DTO also avoids the direct [FromForm] IFormFile Swagger incompatibility.
public sealed class VerificationTransferForm
{
    public IFormFile? File { get; init; }
    public bool AllowWrites { get; init; }
    public string SnapshotToken { get; init; } = "";
    public string Directory { get; init; } = "";
    public string RemoteName { get; init; } = "";
    public long ExpectedBytes { get; init; }
    public string ExpectedSha256 { get; init; } = "";
}
