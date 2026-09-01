using Microsoft.AspNetCore.Mvc;
using MachineConnectionApi.Proxy;
using MachineConnectionApi.Services;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MachineConnectionApi.Controllers;

/// <summary>
/// NC 程序传输 API：将请求转发至 Industrial IoT。
/// </summary>
[ApiController]
[Route("api/program-transfer")]
public class ProgramTransferController : IndustrialIoTProxyControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly INCLinkDeviceResolver _deviceResolver;
    private readonly INCLinkApiClient _ncLinkApiClient;

    public ProgramTransferController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        INCLinkDeviceResolver deviceResolver,
        INCLinkApiClient ncLinkApiClient,
        ILogger<ProgramTransferController> logger)
        : base(httpClientFactory, logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _deviceResolver = deviceResolver;
        _ncLinkApiClient = ncLinkApiClient;
    }

    private string TransferPath =>
        _configuration["IndustrialIoT:ProgramTransferPath"] ?? "api/program-transfer";

    /// <summary>上传 NC 程序到设备（multipart/form-data：file、remotePath）</summary>
    [HttpPost("{deviceId}/upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> Upload(string deviceId, CancellationToken ct)
    {
        var identity = await _deviceResolver.ResolveAsync(deviceId, ct);
        if (identity.IsNCLinkApi)
        {
            if (!identity.HasConfiguredDeviceId)
                return BadRequest(new { error = "NC-Link API Server 须填写 DeviceId（机床 SN 码）" });
            return await UploadNCLinkFileAsync(deviceId, identity.DeviceId, ct);
        }

        return await ProxyForwardAsync(
            HttpMethod.Post,
            $"{TransferPath}/{Uri.EscapeDataString(deviceId)}/upload",
            ct,
            fileResponseOnSuccess: false);
    }

    /// <summary>从设备下载 NC 程序</summary>
    [HttpPost("{deviceId}/download")]
    public async Task<IActionResult> Download(string deviceId, CancellationToken ct)
    {
        var identity = await _deviceResolver.ResolveAsync(deviceId, ct);
        if (identity.IsNCLinkApi)
        {
            if (!identity.HasConfiguredDeviceId)
                return BadRequest(new { error = "NC-Link API Server 须填写 DeviceId（机床 SN 码）" });
            return await DownloadNCLinkFileAsync(deviceId, identity.DeviceId, ct);
        }

        return await ProxyForwardAsync(
            HttpMethod.Post,
            $"{TransferPath}/{Uri.EscapeDataString(deviceId)}/download",
            ct,
            fileResponseOnSuccess: true);
    }

    /// <summary>某设备的传输历史</summary>
    [HttpGet("{deviceId}/history")]
    public Task<IActionResult> History(string deviceId, CancellationToken ct) =>
        ProxyForwardAsync(
            HttpMethod.Get,
            $"{TransferPath}/{Uri.EscapeDataString(deviceId)}/history",
            ct,
            fileResponseOnSuccess: false);

    /// <summary>获取设备程序传输能力</summary>
    [HttpGet("{deviceId}/capabilities")]
    public Task<IActionResult> Capabilities(string deviceId, CancellationToken ct) =>
        ProxyForwardAsync(
            HttpMethod.Get,
            $"{TransferPath}/{Uri.EscapeDataString(deviceId)}/capabilities",
            ct,
            fileResponseOnSuccess: false);

    /// <summary>单条传输记录状态</summary>
    [HttpGet("{transferId}/status")]
    public Task<IActionResult> Status(string transferId, CancellationToken ct) =>
        ProxyForwardAsync(
            HttpMethod.Get,
            $"{TransferPath}/{Uri.EscapeDataString(transferId)}/status",
            ct,
            fileResponseOnSuccess: false);

    /// <summary>断点续传上传</summary>
    [HttpPost("{deviceId}/resume")]
    public Task<IActionResult> Resume(string deviceId, CancellationToken ct) =>
        ProxyForwardAsync(
            HttpMethod.Post,
            $"{TransferPath}/{Uri.EscapeDataString(deviceId)}/resume",
            ct,
            fileResponseOnSuccess: false);

    /// <summary>浏览设备程序目录（原始节点）</summary>
    [HttpGet("{deviceId}/browse")]
    public Task<IActionResult> Browse(string deviceId, CancellationToken ct) =>
        ProxyForwardAsync(
            HttpMethod.Get,
            BuildPathWithQuery($"{TransferPath}/{Uri.EscapeDataString(deviceId)}/browse"),
            ct,
            fileResponseOnSuccess: false);

    /// <summary>获取设备程序文件列表</summary>
    [HttpGet("{deviceId}/files")]
    public async Task<IActionResult> Files(string deviceId, CancellationToken ct)
    {
        var identity = await _deviceResolver.ResolveAsync(deviceId, ct);
        if (identity.IsNCLinkApi)
        {
            if (!identity.HasConfiguredDeviceId)
                return BadRequest(new { error = "NC-Link API Server 须填写 DeviceId（机床 SN 码）" });
            return await ListNCLinkFilesAsync(identity.DeviceId, ct);
        }

        return await ProxyForwardAsync(
            HttpMethod.Get,
            BuildPathWithQuery($"{TransferPath}/{Uri.EscapeDataString(deviceId)}/files"),
            ct,
            fileResponseOnSuccess: false);
    }

    /// <summary>批量下载程序</summary>
    [HttpPost("{deviceId}/download-batch")]
    public async Task<IActionResult> DownloadBatch(string deviceId, CancellationToken ct)
    {
        var identity = await _deviceResolver.ResolveAsync(deviceId, ct);
        if (identity.IsNCLinkApi)
            return StatusCode(StatusCodes.Status501NotImplemented,
                new { error = "NC-Link API 暂不支持批量下载，请选择单个文件 key 下载" });

        return await ProxyForwardAsync(
            HttpMethod.Post,
            $"{TransferPath}/{Uri.EscapeDataString(deviceId)}/download-batch",
            ct,
            fileResponseOnSuccess: false);
    }

    /// <summary>批量上传程序（multipart/form-data：remotePath、files）</summary>
    [HttpPost("{deviceId}/upload-batch")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(500_000_000)]
    public async Task<IActionResult> UploadBatch(string deviceId, CancellationToken ct)
    {
        var identity = await _deviceResolver.ResolveAsync(deviceId, ct);
        if (identity.IsNCLinkApi)
            return StatusCode(StatusCodes.Status501NotImplemented,
                new { error = "NC-Link API 暂不支持批量上传，请一次上传一个文件" });

        return await ProxyForwardAsync(
            HttpMethod.Post,
            $"{TransferPath}/{Uri.EscapeDataString(deviceId)}/upload-batch",
            ct,
            fileResponseOnSuccess: false);
    }

    /// <summary>查询批量任务状态</summary>
    [HttpGet("tasks/{taskId}")]
    public Task<IActionResult> TaskStatus(string taskId, CancellationToken ct) =>
        ProxyForwardAsync(
            HttpMethod.Get,
            $"{TransferPath}/tasks/{Uri.EscapeDataString(taskId)}",
            ct,
            fileResponseOnSuccess: false);

    /// <summary>下载批量任务制品（ZIP/报告）</summary>
    [HttpGet("tasks/{taskId}/artifact")]
    public Task<IActionResult> TaskArtifact(string taskId, CancellationToken ct) =>
        ProxyForwardAsync(
            HttpMethod.Get,
            $"{TransferPath}/tasks/{Uri.EscapeDataString(taskId)}/artifact",
            ct,
            fileResponseOnSuccess: true);

    private async Task<IActionResult> UploadNCLinkFileAsync(
        string uiDeviceId, string ncLinkDeviceId, CancellationToken ct)
    {
        var form = await Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        var remotePath = form["remotePath"].ToString().Trim();
        if (file is null || string.IsNullOrWhiteSpace(remotePath))
            return BadRequest(new { error = "file and remotePath required" });

        var key = BuildNCLinkFileKey(remotePath, file.FileName);
        await using var stream = file.OpenReadStream();
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(key, Encoding.UTF8), "key");
        content.Add(new StringContent("set_value", Encoding.UTF8), "operation");
        content.Add(new StringContent("/MACHINE/CONTROLLER/FILE", Encoding.UTF8), "path");
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new(file.ContentType ?? MediaTypeNames.Application.Octet);
        content.Add(fileContent, "value", file.FileName);

        using var response = await SendNCLinkFileAsync(
            HttpMethod.Post, ncLinkDeviceId, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!IsNCLinkSuccess(text))
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "NC-Link 文件上传失败", detail = text });

        return Ok(BuildTransferResponse(uiDeviceId, file.FileName, key,
            "Upload", file.Length, null));
    }

    private async Task<IActionResult> ListNCLinkFilesAsync(
        string ncLinkDeviceId, CancellationToken ct)
    {
        var response = await _ncLinkApiClient.BatchDataAsync(
            ncLinkDeviceId,
            "get_keys",
            new[] { new NCLinkApiRequestItem { Path = "/MACHINE/CONTROLLER/FILE" } },
            ct);
        if (!response.IsSuccess)
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "NC-Link 文件列表获取失败", detail = $"code={response.Code}" });

        var keys = ExtractNCLinkKeys(response.Value).ToList();
        return Ok(keys.Select(k => new
        {
            name = Path.GetFileName(k.Replace('\\', '/')),
            path = k,
            nodeType = "file",
        }));
    }

    private async Task<IActionResult> DownloadNCLinkFileAsync(
        string uiDeviceId, string ncLinkDeviceId, CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var remotePath = doc.RootElement.TryGetProperty("remotePath", out var p)
            ? p.GetString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(remotePath))
            return BadRequest(new { error = "remotePath required" });

        var path = $"v1/{Uri.EscapeDataString(ncLinkDeviceId)}/file/?key={Uri.EscapeDataString(remotePath)}";
        var client = _httpClientFactory.CreateClient("NCLinkApi");
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            return StatusCode((int)response.StatusCode,
                new { error = "NC-Link 文件下载失败", detail = await response.Content.ReadAsStringAsync(ct) });

        var media = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(media, MediaTypeNames.Application.Json, StringComparison.OrdinalIgnoreCase))
        {
            var stream = await response.Content.ReadAsStreamAsync(ct);
            return File(stream, media ?? MediaTypeNames.Application.Octet, Path.GetFileName(remotePath));
        }

        return BuildFileFromNCLinkJson(
            await response.Content.ReadAsStringAsync(ct), remotePath);
    }

    private async Task<HttpResponseMessage> SendNCLinkFileAsync(
        HttpMethod method, string deviceId, HttpContent content, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("NCLinkApi");
        var path = $"v1/{Uri.EscapeDataString(deviceId)}/file/";
        using var request = new HttpRequestMessage(method, path) { Content = content };
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static string BuildNCLinkFileKey(string remotePath, string fileName)
    {
        var key = remotePath.Trim().Replace('\\', '/');
        if (key.EndsWith("/", StringComparison.Ordinal))
            key += fileName;
        return key;
    }

    private static object BuildTransferResponse(
        string deviceId, string fileName, string remotePath, string direction, long size, string? error)
    {
        var now = DateTimeOffset.UtcNow;
        return new
        {
            transferId = Guid.NewGuid().ToString("N"),
            deviceId,
            fileName,
            remotePath,
            direction,
            status = error is null ? "Completed" : "Failed",
            fileSize = size,
            bytesTransferred = error is null ? size : 0,
            startedAt = now,
            completedAt = now,
            errorMessage = error,
        };
    }

    private IActionResult BuildFileFromNCLinkJson(string json, string remotePath)
    {
        if (!IsNCLinkSuccess(json))
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "NC-Link 文件下载失败", detail = json });

        using var doc = JsonDocument.Parse(json);
        var value = doc.RootElement.TryGetProperty("value", out var v)
            ? v
            : default;
        var text = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : value.GetRawText();
        var bytes = Encoding.UTF8.GetBytes(text);
        return File(bytes, MediaTypeNames.Application.Octet, Path.GetFileName(remotePath));
    }

    private static IEnumerable<string> ExtractNCLinkKeys(JsonNode? node)
    {
        if (node is not JsonArray outer)
            yield break;
        foreach (var group in outer)
        {
            if (group is JsonArray inner)
            {
                foreach (var item in inner)
                    if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                        yield return s;
            }
            else if (group is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            {
                yield return s;
            }
        }
    }

    private static bool IsNCLinkSuccess(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : -1;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : "";
            return code == 0 && string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private string BuildPathWithQuery(string basePath)
    {
        var query = Request.QueryString.Value;
        return string.IsNullOrEmpty(query) ? basePath : $"{basePath}{query}";
    }
}
