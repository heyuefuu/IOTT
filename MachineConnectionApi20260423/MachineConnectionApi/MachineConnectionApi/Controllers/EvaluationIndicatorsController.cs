namespace MachineConnectionApi.Controllers;

using System.Text.Json;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/evaluation")]
public sealed class EvaluationIndicatorsController : ControllerBase
{
    private const long MaxAttachmentBytes = 100 * 1024 * 1024;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".nc", ".ncg", ".txt", ".xml", ".prg" };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly EvaluationIndicatorStore _store;
    private readonly string _attachmentDirectory = Path.Combine(AppContext.BaseDirectory, "App_Data", "evaluation-attachments");

    public EvaluationIndicatorsController(EvaluationIndicatorStore store) => _store = store;

    [HttpPost("attachments")]
    [RequestSizeLimit(MaxAttachmentBytes + 65536)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxAttachmentBytes + 65536)]
    public async Task<ActionResult<EvaluationFile>> Upload(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length <= 0 || file.Length > MaxAttachmentBytes)
            return BadRequest(new { message = "请选择有效测试文件，单个文件不超过 100 MB。" });
        var name = Path.GetFileName(file.FileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !AllowedExtensions.Contains(Path.GetExtension(name)))
            return BadRequest(new { message = "支持上传 .nc、.ncg、.txt、.xml、.prg 格式的测试文件。" });
        Directory.CreateDirectory(_attachmentDirectory);
        var id = Guid.NewGuid().ToString("N");
        var binaryPath = Path.Combine(_attachmentDirectory, $"{id}.bin");
        var metadataPath = Path.Combine(_attachmentDirectory, $"{id}.json");
        var completed = false;
        try
        {
            await using (var stream = new FileStream(binaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.CopyToAsync(stream, cancellationToken);
                if (stream.Length != file.Length || stream.Length > MaxAttachmentBytes)
                    return BadRequest(new { message = "测试文件大小不一致，请重新上传。" });
            }
            var metadata = new EvaluationFile { Id = id, Name = name, SizeBytes = file.Length, Size = $"{file.Length / 1024d / 1024d:0.##} MB" };
            await System.IO.File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions), cancellationToken);
            completed = true;
            return Ok(metadata);
        }
        finally
        {
            if (!completed)
            {
                if (System.IO.File.Exists(binaryPath)) System.IO.File.Delete(binaryPath);
                if (System.IO.File.Exists(metadataPath)) System.IO.File.Delete(metadataPath);
            }
        }
    }

    [HttpGet("attachments/{id}")]
    public async Task<IActionResult> Download(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(id, "N", out var parsedId)) return NotFound(new { message = "测试文件不存在。" });
        var safeId = parsedId.ToString("N");
        var binaryPath = Path.Combine(_attachmentDirectory, $"{safeId}.bin");
        var metadataPath = Path.Combine(_attachmentDirectory, $"{safeId}.json");
        if (!System.IO.File.Exists(binaryPath) || !System.IO.File.Exists(metadataPath))
            return NotFound(new { message = "测试文件不存在或尚未上传。" });
        var metadata = JsonSerializer.Deserialize<EvaluationFile>(await System.IO.File.ReadAllTextAsync(metadataPath, cancellationToken), JsonOptions);
        if (metadata is null || metadata.Id != safeId) return Problem("测试文件信息损坏，请重新上传。");
        return PhysicalFile(binaryPath, "application/octet-stream", metadata.Name);
    }

    [HttpGet("indicators/{category}")]
    public ActionResult<EvaluationConfig> Get(string category)
    {
        try { return Ok(_store.Get(category)); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
        catch (Exception error) when (error is IOException or JsonException)
        { return Problem("评价指标数据读取失败，请检查存储文件。"); }
    }

    [HttpPut("indicators/{category}")]
    public ActionResult<EvaluationConfig> Save(string category, [FromBody] EvaluationConfig config)
    {
        if (config.Category != category) return BadRequest(new { message = "评价指标分类与保存目标不一致。" });
        try { return Ok(_store.Save(config)); }
        catch (EvaluationConfigConflictException error) { return Conflict(new { message = error.Message }); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
        catch (Exception error) when (error is IOException or JsonException)
        { return Problem("评价指标保存失败，请保留当前修改后重试。"); }
    }
}
