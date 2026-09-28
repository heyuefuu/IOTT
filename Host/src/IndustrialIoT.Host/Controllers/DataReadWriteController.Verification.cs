namespace IndustrialIoT.Host.Controllers;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Host.Services;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using Microsoft.AspNetCore.Mvc;

public partial class DataReadWriteController
{
    private async Task<ActionResult<ReadTagsResponse>> ReadVerifiedTags(string deviceId, VerificationExpectation expected,
        IReadOnlyList<TagReadRequest> tags, CancellationToken ct)
    {
        if (_verification is null)
            return StatusCode(503, new { deviceId, error = "Verification configuration executor is unavailable." });
        var result = await _verification.ExecuteAsync(deviceId, expected, false, "application-read",
            protocol => protocol is not (ProtocolType.FTP or ProtocolType.SMB or ProtocolType.NFS or ProtocolType.Serial or ProtocolType.GskrmFileTransfer),
            driver => driver.Capabilities.HasFlag(DriverCapabilities.Read),
            driver => driver.ReadTagsAsync(tags, ct), ct);
        if (result.StatusCode != 200)
            return StatusCode(result.StatusCode, new { deviceId, error = result.Error, evidence = result.Evidence });
        return Ok(new ReadTagsResponse
        {
            DeviceId = deviceId, Evidence = result.Evidence,
            Tags = result.Value!.Select(value => new TagValueDto
            {
                Address = value.Address, DataType = value.DataType.ToString(), Value = value.Value,
                Quality = value.Quality.ToString(), Timestamp = value.Timestamp,
                // An SDK error can contain connection strings/credentials. Use a safe code on the
                // verification endpoint while preserving the unsupported distinction for scoring.
                ErrorMessage = string.IsNullOrWhiteSpace(value.ErrorMessage) ? null
                    : IsUnsupportedRead(value.ErrorMessage) ? "Read not supported by driver." : "Device read returned an error.",
            }).ToList(),
        });
    }

    /// <summary>只读验证目录；期望配置和实际驱动由服务端核验，不经历史记录或上传接口。</summary>
    [HttpPost("{deviceId}/verification-directory")]
    public async Task<IActionResult> ReadVerificationDirectory(string deviceId,
        [FromBody] VerificationDirectoryRequest request, CancellationToken ct)
    {
        if (request.Verification is null || string.IsNullOrWhiteSpace(request.Directory) || request.Directory.Length > 1024
            || request.Directory.Any(char.IsControl)
            || request.Directory.Replace('\\', '/').Split('/').Any(part => part is "." or ".."))
            return BadRequest(new { deviceId, error = "A safe directory and expected configuration are required." });
        if (_verification is null)
            return StatusCode(503, new { deviceId, error = "Verification configuration executor is unavailable." });
        var path = request.Directory.Replace('\\', '/').TrimEnd('/');
        var result = await _verification.ExecuteAsync(deviceId, request.Verification, true, "directory-read",
            // NFS initialization can create a local mount directory; static roots in other drivers
            // do not constitute protocol I/O. Reject these before ConnectAsync, not after it.
            protocol => protocol is ProtocolType.FTP or ProtocolType.SMB or ProtocolType.NCLink or ProtocolType.NCLinkApi
                || protocol == ProtocolType.FOCAS && path.Equals("/Programs", StringComparison.OrdinalIgnoreCase),
            driver => driver is IAddressSpaceBrowser && driver is INCProgramTransfer,
            driver => new ProgramTransferFileBrowserService().BrowseAsync((IAddressSpaceBrowser)driver, request.Directory, false, ct), ct);
        if (result.StatusCode != 200)
            return StatusCode(result.StatusCode, new { deviceId, error = result.Error, evidence = result.Evidence });
        // These drivers collapse directory errors to []; do not treat it as an empty successful list.
        if (result.Value!.Count == 0 && result.Evidence.Protocol is "FOCAS" or "SMB")
            return StatusCode(422, new { deviceId, error = "Empty directory cannot be distinguished from a driver read failure.",
                evidence = result.Evidence with { Code = "ambiguous-empty-directory" } });
        return Ok(new { deviceId, evidence = result.Evidence, files = result.Value });
    }

    private static bool IsUnsupportedRead(string error) => error.Contains("not support", StringComparison.OrdinalIgnoreCase)
        || error.Contains("unsupported", StringComparison.OrdinalIgnoreCase) || error.Contains("不支持", StringComparison.Ordinal);
}

public sealed record VerificationDirectoryRequest
{
    public string Directory { get; init; } = "";
    public VerificationExpectation? Verification { get; init; }
}
