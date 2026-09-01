using System.Net.Mime;
using Microsoft.AspNetCore.Mvc;

namespace MachineConnectionApi.Proxy;

public abstract class IndustrialIoTProxyControllerBase : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    protected IndustrialIoTProxyControllerBase(
        IHttpClientFactory httpClientFactory,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected async Task<string> ReadRequestBodyAsync(CancellationToken ct)
    {
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        Request.Body.Position = 0;
        return body;
    }

    protected StringContent CreateJsonContent(string body)
    {
        var mediaType = Request.ContentType?.Split(';')[0].Trim();
        if (string.IsNullOrEmpty(mediaType))
            mediaType = MediaTypeNames.Application.Json;
        return new StringContent(body, System.Text.Encoding.UTF8, mediaType);
    }

    protected async Task<IActionResult> ProxyTextAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("IndustrialIoT");
        using var request = new HttpRequestMessage(method, relativePath);
        if (content != null)
            request.Content = content;

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Industrial IoT 转发失败: {Path}", relativePath);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Industrial IoT 服务不可用", detail = ex.Message });
        }

        using (response)
        {
            if (response.Headers.Location is not null)
                Response.Headers.Location = response.Headers.Location.ToString();

            var text = await response.Content.ReadAsStringAsync(ct);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Application.Json;

            return new ContentResult
            {
                StatusCode = (int)response.StatusCode,
                Content = text,
                ContentType = mediaType
            };
        }
    }

    protected async Task<IActionResult> ProxyFileAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("IndustrialIoT");
        using var request = new HttpRequestMessage(method, relativePath);
        if (content != null)
            request.Content = content;

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Industrial IoT 文件转发失败: {Path}", relativePath);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Industrial IoT 服务不可用", detail = ex.Message });
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Application.Json;
                return new ContentResult
                {
                    StatusCode = (int)response.StatusCode,
                    Content = text,
                    ContentType = mediaType
                };
            }

            // 必须在 using(response) 释放前把内容读进内存：File(stream,...) 是延迟执行，
            // action 返回后框架才读流，那时 response 已 Dispose、流已关闭 → 下载失败。
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var mediaTypeOk = response.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Application.Octet;

            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                           ?? response.Content.Headers.ContentDisposition?.FileName;
            fileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim('"');

            return fileName is null
                ? File(bytes, mediaTypeOk)
                : File(bytes, mediaTypeOk, fileName);
        }
    }

    /// <summary>
    /// 转发当前请求的 Body（含 Content-Type）到 Industrial IoT，用于 multipart 上传等场景。
    /// </summary>
    protected async Task<IActionResult> ProxyForwardAsync(
        HttpMethod method,
        string relativePath,
        CancellationToken ct,
        bool fileResponseOnSuccess = false)
    {
        Request.EnableBuffering();
        Request.Body.Position = 0;

        var client = _httpClientFactory.CreateClient("IndustrialIoT");
        using var outbound = new HttpRequestMessage(method, relativePath);

        if (method != HttpMethod.Get &&
            method != HttpMethod.Delete &&
            !string.IsNullOrEmpty(Request.ContentType))
        {
            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync(ct);
                var multipart = new MultipartFormDataContent();
                foreach (var field in form)
                    foreach (var value in field.Value)
                        multipart.Add(new StringContent(value ?? string.Empty), field.Key);

                foreach (var file in form.Files)
                {
                    var fileContent = new StreamContent(file.OpenReadStream());
                    if (!string.IsNullOrWhiteSpace(file.ContentType))
                        fileContent.Headers.ContentType =
                            System.Net.Http.Headers.MediaTypeHeaderValue.Parse(file.ContentType);
                    multipart.Add(fileContent, file.Name, file.FileName);
                }
                outbound.Content = multipart;
            }
            else
            {
                // 通过异步复制请求体，避免触发 ASP.NET Core 对同步 Read 的限制。
                await using var bodyBuffer = new MemoryStream();
                await Request.Body.CopyToAsync(bodyBuffer, ct);
                outbound.Content = new ByteArrayContent(bodyBuffer.ToArray());
                outbound.Content.Headers.ContentType =
                    System.Net.Http.Headers.MediaTypeHeaderValue.Parse(Request.ContentType);
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Industrial IoT 转发失败: {Path}", relativePath);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Industrial IoT 服务不可用", detail = ex.Message });
        }

        using (response)
        {
            if (response.Headers.Location is not null)
                Response.Headers.Location = response.Headers.Location.ToString();

            if (fileResponseOnSuccess &&
                response.IsSuccessStatusCode &&
                string.Equals(
                    response.Content.Headers.ContentType?.MediaType,
                    MediaTypeNames.Application.Octet,
                    StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                var mediaTypeOk = response.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Application.Octet;
                var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                               ?? response.Content.Headers.ContentDisposition?.FileName;
                fileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim('"');
                return fileName is null
                    ? File(bytes, mediaTypeOk)
                    : File(bytes, mediaTypeOk, fileName);
            }

            var text = await response.Content.ReadAsStringAsync(ct);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Application.Json;

            return new ContentResult
            {
                StatusCode = (int)response.StatusCode,
                Content = text,
                ContentType = mediaType
            };
        }
    }
}
