using Microsoft.AspNetCore.Mvc;
using MachineConnectionApi.Proxy;

namespace MachineConnectionApi.Controllers;

/// <summary>
/// NC 程序传输 API：将请求转发至 Industrial IoT。
/// </summary>
[ApiController]
[Route("api/program-transfer")]
public class ProgramTransferController : IndustrialIoTProxyControllerBase
{
    private readonly IConfiguration _configuration;

    public ProgramTransferController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ProgramTransferController> logger)
        : base(httpClientFactory, logger)
    {
        _configuration = configuration;
    }

    private string TransferPath =>
        _configuration["IndustrialIoT:ProgramTransferPath"] ?? "api/program-transfer";

    /// <summary>上传 NC 程序到设备（multipart/form-data：file、remotePath）</summary>
    [HttpPost("{deviceId}/upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> Upload(string deviceId, CancellationToken ct)
    {
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

    private string BuildPathWithQuery(string basePath)
    {
        var query = Request.QueryString.Value;
        return string.IsNullOrEmpty(query) ? basePath : $"{basePath}{query}";
    }
}
