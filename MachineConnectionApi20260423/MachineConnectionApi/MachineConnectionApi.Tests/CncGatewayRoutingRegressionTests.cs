using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MachineConnectionApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MachineConnectionApi.Tests;

internal static partial class CncGatewayRoutingRegressionTests
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NCLinkApi:BaseUrl"] = "http://unused-nclink.invalid:19001/",
            ["MQTT:Host"] = "unused-mqtt.invalid",
            ["MQTT:Enabled"] = "true",
        }).Build();

    private static readonly CncDevice[] Devices =
    [
        new("NCLinkApi", "hnc-device", "/MACHINE/CONTROLLER/VARIABLE", "/MACHINE/CONTROLLER/VARIABLE@REG_X?index=0"),
        new("GskWebServer", "gsk-device", "/Realtime", "/Realtime.Mode"),
    ];

    public static async Task RunAll()
    {
        foreach (var device in Devices)
        {
            await DataRequestsUseHost(device);
            await BrowseUsesHost(device);
            await TransferRequestsUseHost(device);
            await UploadPreservesMultipart(device);
            await DownloadPreservesBytes(device);
            await UpstreamFailureDoesNotUseFallback(device);
        }
        await LegacyCollectionUsesHost();
        Console.WriteLine("CNC gateway routing: data, browse, transfer, and legacy collection passed.");
    }

    private static async Task DataRequestsUseHost(CncDevice device)
    {
        foreach (var operation in new[] { "read", "write" })
        {
            var payload = JsonSerializer.Serialize(new { tags = new[]
                { new { address = device.Address, dataType = "Int32", sourceId = "old-mqtt-source", value = 1 } } });
            var response = operation == "write"
                ? "{\"totalCount\":1,\"successCount\":0,\"results\":[{\"success\":false,\"errorMessage\":\"device rejected write\"}]}"
                : "{\"tags\":[{\"value\":0,\"quality\":\"Good\"}]}";
            using var host = new HostFixture { ResponseText = response };
            var controller = DataController(host, payload);
            var result = operation == "read"
                ? await controller.ReadTags(device.Id, CancellationToken.None)
                : await controller.WriteTags(device.Id, CancellationToken.None);
            AssertContent(result, 200, response);
            var request = AssertRequest(host, HttpMethod.Post, $"/api/data/{device.Id}/{operation}");
            if (Encoding.UTF8.GetString(request.Body) != payload || request.ContentType != "application/json")
                throw new InvalidOperationException("Data payload, including SourceId, was rewritten.");
        }
    }

    private static async Task BrowseUsesHost(CncDevice device)
    {
        var response = JsonSerializer.Serialize(new[] { new { path = device.Address, nodeType = "Variable", sourceId = "retained-id" } });
        using var host = new HostFixture { ResponseText = response };
        var controller = WithRequest(new AddressSpaceController(host, Configuration, NullLogger<AddressSpaceController>.Instance));
        var result = await controller.Browse(device.Id, device.Parent, device.Protocol, CancellationToken.None);
        AssertRequest(host, HttpMethod.Get, $"/api/addressspace/{device.Id}?parentPath={Uri.EscapeDataString(device.Parent)}");
        if (result is not ContentResult { StatusCode: 200 } content)
            throw new InvalidOperationException("CNC address browse failed.");
        using var document = JsonDocument.Parse(content.Content!);
        if (document.RootElement.GetArrayLength() != 1 ||
            document.RootElement[0].GetProperty("path").GetString() != device.Address ||
            document.RootElement[0].GetProperty("sourceId").GetString() != "retained-id")
            throw new InvalidOperationException("CNC variable child or its metadata was lost.");
    }

    private static async Task UpstreamFailureDoesNotUseFallback(CncDevice device)
    {
        foreach (var (transportFailure, failure) in new[]
            { (false, "{\"error\":\"configured device is offline\"}"), (false, "[]"), (true, "") })
        foreach (var operation in new[] { "read", "write", "browse", "files", "download", "download-batch" })
        {
            using var host = new HostFixture { Status = HttpStatusCode.UnprocessableEntity, ResponseText = failure, ThrowOnSend = transportFailure };
            var data = DataController(host, "{\"tags\":[{\"address\":\"status\",\"sourceId\":\"old-id\"}]}");
            var transfer = TransferController(host, "{\"remotePath\":\"O0001\",\"paths\":[\"O0001\"]}");
            var browse = WithRequest(new AddressSpaceController(host, Configuration, NullLogger<AddressSpaceController>.Instance));
            var result = operation switch
            {
                "read" => await data.ReadTags(device.Id, CancellationToken.None),
                "write" => await data.WriteTags(device.Id, CancellationToken.None),
                "browse" => await browse.Browse(device.Id, device.Parent, device.Protocol, CancellationToken.None),
                "files" => await transfer.Files(device.Id, CancellationToken.None),
                "download" => await transfer.Download(device.Id, CancellationToken.None),
                _ => await transfer.DownloadBatch(device.Id, CancellationToken.None),
            };
            if (transportFailure)
            {
                if (result is not ObjectResult { StatusCode: 502 })
                    throw new InvalidOperationException("Unavailable Host must return HTTP 502.");
            }
            else AssertContent(result, 422, failure);
            if (host.ClientNames.Count != 1 || host.ClientNames[0] != "IndustrialIoT" || host.Requests.Count != 1)
                throw new InvalidOperationException("Failed request triggered alternate NCLink routing.");
        }
    }

    private static async Task TransferRequestsUseHost(CncDevice device)
    {
        const string query = "?path=%2Fprograms%2F&recursive=true";
        const string body = "{\"paths\":[\"O0001\",\"O0002\"],\"remotePath\":\"O0001\"}";
        var token = CancellationToken.None;
        (HttpMethod Method, string Path, Func<ProgramTransferController, Task<IActionResult>> Run)[] routes =
        [
            (HttpMethod.Get, $"{device.Id}/files{query}", controller => controller.Files(device.Id, token)),
            (HttpMethod.Get, $"{device.Id}/browse{query}", controller => controller.Browse(device.Id, token)),
            (HttpMethod.Get, $"{device.Id}/history", controller => controller.History(device.Id, token)),
            (HttpMethod.Get, $"{device.Id}/capabilities", controller => controller.Capabilities(device.Id, token)),
            (HttpMethod.Get, "transfer-1/status", controller => controller.Status("transfer-1", token)),
            (HttpMethod.Post, $"{device.Id}/resume", controller => controller.Resume(device.Id, token)),
            (HttpMethod.Post, $"{device.Id}/download-batch", controller => controller.DownloadBatch(device.Id, token)),
            (HttpMethod.Get, "tasks/task-1", controller => controller.TaskStatus("task-1", token)),
        ];
        foreach (var route in routes)
        {
            const string response = "{\"taskId\":\"task-1\"}";
            using var host = new HostFixture { ResponseText = response };
            if (route.Path.EndsWith("-batch")) host.Status = HttpStatusCode.Accepted;
            var result = await route.Run(TransferController(host, body, query));
            AssertContent(result, (int)host.Status, response);
            var request = AssertRequest(host, route.Method, $"/api/program-transfer/{route.Path}");
            if (route.Method == HttpMethod.Post && Encoding.UTF8.GetString(request.Body) != body)
                throw new InvalidOperationException("Transfer request payload was rewritten.");
        }
    }

    private static async Task UploadPreservesMultipart(CncDevice device)
    {
        foreach (var batch in new[] { false, true })
        {
            const string remotePath = "programs/O0001";
            byte[] bytes = [0x25, 0x00, 0xff, 0x0d, 0x0a];
            using var host = new HostFixture { ResponseText = "{\"status\":\"Completed\"}" };
            var controller = TransferController(host);
            var files = new FormFileCollection();
            for (var index = 0; index < (batch ? 2 : 1); index++)
                files.Add(new FormFile(new MemoryStream(bytes), 0, bytes.Length, batch ? "files" : "file", $"part-{index}.nc")
                    { Headers = new HeaderDictionary(), ContentType = "application/octet-stream" });
            controller.Request.ContentType = "multipart/form-data; boundary=incoming-fixture";
            controller.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
                { ["remotePath"] = remotePath, ["metadata"] = new[] { "first", "second" } }, files);
            var result = batch
                ? await controller.UploadBatch(device.Id, CancellationToken.None)
                : await controller.Upload(device.Id, CancellationToken.None);
            AssertContent(result, 200, "{\"status\":\"Completed\"}");
            var request = AssertRequest(host, HttpMethod.Post, $"/api/program-transfer/{device.Id}/{(batch ? "upload-batch" : "upload")}");
            var uploaded = request.Parts.Where(part => part.FileName is not null).ToList();
            if (request.ContentType != "multipart/form-data" || uploaded.Count != files.Count ||
                uploaded.Any(part => !part.Body.SequenceEqual(bytes) || part.ContentType != "application/octet-stream") ||
                request.Parts.Count(part => part.Name == "metadata") != 2 ||
                Encoding.UTF8.GetString(request.Parts.Single(part => part.Name == "remotePath").Body) != remotePath)
                throw new InvalidOperationException("Multipart file bytes, repeated fields, or remotePath were lost.");
        }
    }

    private static async Task DownloadPreservesBytes(CncDevice device)
    {
        foreach (var artifact in new[] { false, true })
        foreach (var mediaType in new[] { "application/octet-stream", "text/plain", "application/zip", "application/json" })
        {
            byte[] bytes = [0xff, 0xfe, 0x00, 0x80, 0x0d, 0x0a];
            using var host = new HostFixture { ResponseBytes = bytes, ResponseType = mediaType, FileName = "part program.nc" };
            var controller = TransferController(host, "{\"remotePath\":\"O0001\"}");
            var result = artifact
                ? await controller.TaskArtifact("task-1", CancellationToken.None)
                : await controller.Download(device.Id, CancellationToken.None);
            AssertRequest(host, artifact ? HttpMethod.Get : HttpMethod.Post,
                artifact ? "/api/program-transfer/tasks/task-1/artifact" : $"/api/program-transfer/{device.Id}/download");
            if (!host.ResponseDisposed || result is not FileContentResult file ||
                !file.FileContents.SequenceEqual(bytes) || file.ContentType != mediaType || file.FileDownloadName != host.FileName)
                throw new InvalidOperationException("Download bytes or filename did not survive upstream disposal.");
        }
    }

    private static T WithRequest<T>(T controller, string body = "", string? contentType = "application/json",
        string query = "") where T : ControllerBase
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentType = contentType;
        context.Request.QueryString = new QueryString(query);
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static DataReadWriteController DataController(HostFixture host, string body) => WithRequest(
        new DataReadWriteController(host, Configuration, NullLogger<DataReadWriteController>.Instance), body);

    private static ProgramTransferController TransferController(HostFixture host, string body = "", string query = "") => WithRequest(
        new ProgramTransferController(host, Configuration, NullLogger<ProgramTransferController>.Instance), body, query: query);

    private static void AssertContent(IActionResult result, int status, string body)
    {
        if (result is not ContentResult content || content.StatusCode != status || content.Content != body)
            throw new InvalidOperationException($"Expected unchanged HTTP {status} response: {body}");
    }

    private static RequestSnapshot AssertRequest(HostFixture host, HttpMethod method, string path)
    {
        if (host.ClientNames.Count != 1 || host.ClientNames[0] != "IndustrialIoT" || host.Requests.Count != 1)
            throw new InvalidOperationException("Expected exactly one IndustrialIoT request and no alternate client.");
        var request = host.Requests[0];
        if (request.Method != method || request.Uri.PathAndQuery != path)
            throw new InvalidOperationException($"Expected {method} {path}, got {request.Method} {request.Uri.PathAndQuery}");
        return request;
    }

    private sealed record CncDevice(string Protocol, string Id, string Parent, string Address);
    private sealed record RequestSnapshot(HttpMethod Method, Uri Uri, byte[] Body, string? ContentType, IReadOnlyList<PartSnapshot> Parts);
    private sealed record PartSnapshot(string Name, string? FileName, string? ContentType, byte[] Body);

    private sealed class HostFixture : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient client;
        public List<string> ClientNames { get; } = [];
        public List<RequestSnapshot> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public byte[] ResponseBytes { get; set; } = Encoding.UTF8.GetBytes("[]");
        public string ResponseType { get; set; } = "application/json";
        public string? FileName { get; set; }
        public bool ThrowOnSend { get; set; }
        public bool ResponseDisposed { get; private set; }
        public string ResponseText { set => ResponseBytes = Encoding.UTF8.GetBytes(value); }

        public HostFixture() => client = new HttpClient(new Handler(this)) { BaseAddress = new Uri("http://host.fixture/") };
        public HttpClient CreateClient(string name)
        {
            ClientNames.Add(name);
            if (name != "IndustrialIoT") throw new InvalidOperationException($"Unexpected alternate client: {name}");
            return client;
        }
        public void Dispose() => client.Dispose();

        private sealed class Handler(HostFixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var parts = new List<PartSnapshot>();
                if (request.Content is MultipartContent multipart)
                    foreach (var part in multipart)
                        parts.Add(new(part.Headers.ContentDisposition!.Name!.Trim('"'),
                            part.Headers.ContentDisposition.FileName?.Trim('"'), part.Headers.ContentType?.MediaType,
                            await part.ReadAsByteArrayAsync(ct)));
                fixture.Requests.Add(new(request.Method, request.RequestUri!,
                    request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct),
                    request.Content?.Headers.ContentType?.MediaType, parts));
                if (fixture.ThrowOnSend) throw new HttpRequestException("Host unavailable");
                var content = new TrackedContent(fixture.ResponseBytes, () => fixture.ResponseDisposed = true);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(fixture.ResponseType);
                if (fixture.FileName is not null)
                    content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = fixture.FileName };
                return new HttpResponseMessage(fixture.Status) { Content = content };
            }
        }
    }

    private sealed class TrackedContent(byte[] bytes, Action disposed) : ByteArrayContent(bytes)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) disposed();
            base.Dispose(disposing);
        }
    }
}
