namespace IndustrialIoT.Protocols.HncSdk;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

public interface IHncSdkClient
{
    Task<HncSdkConnectResult> ConnectAsync(HncSdkConnectRequest request, CancellationToken ct);
    Task<HncSdkIpcResult> DisconnectAsync(string sessionId, CancellationToken ct);
    Task<HncSdkIpcResult> PingAsync(string sessionId, CancellationToken ct);
    Task<HncSdkValueResult<object>> ReadAsync(HncSdkReadRequest request, CancellationToken ct);
    Task<HncSdkIpcResult> WriteAsync(HncSdkWriteRequest request, CancellationToken ct);
    Task<HncSdkValueResult<IReadOnlyList<HncSdkFileEntry>>> BrowseFilesAsync(HncSdkBrowseRequest request, CancellationToken ct);
    Task<HncSdkIpcResult> UploadAsync(HncSdkTransferRequest request, CancellationToken ct);
    Task<HncSdkIpcResult> DownloadAsync(HncSdkTransferRequest request, CancellationToken ct);
    Task<HncSdkIpcResult> RemoveAsync(HncSdkRemoveRequest request, CancellationToken ct);
    Task<HncSdkIpcResult> RenameAsync(HncSdkRenameRequest request, CancellationToken ct);
}

public sealed class HncSdkIpcClient : IHncSdkClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient httpClient;

    public HncSdkIpcClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public HncSdkIpcClient(Uri baseAddress)
        : this(new HttpClient { BaseAddress = baseAddress })
    {
    }

    public Task<HncSdkConnectResult> ConnectAsync(HncSdkConnectRequest request, CancellationToken ct)
        => PostAsync<HncSdkConnectResult>("api/hnc-sdk/connect", request, ct);
    public Task<HncSdkIpcResult> DisconnectAsync(string sessionId, CancellationToken ct)
        => PostAsync<HncSdkIpcResult>("api/hnc-sdk/disconnect", new HncSdkSessionRequest(sessionId), ct);
    public Task<HncSdkIpcResult> PingAsync(string sessionId, CancellationToken ct)
        => PostAsync<HncSdkIpcResult>("api/hnc-sdk/ping", new HncSdkSessionRequest(sessionId), ct);
    public Task<HncSdkValueResult<object>> ReadAsync(HncSdkReadRequest request, CancellationToken ct)
        => PostAsync<HncSdkValueResult<object>>("api/hnc-sdk/read", request, ct);
    public Task<HncSdkIpcResult> WriteAsync(HncSdkWriteRequest request, CancellationToken ct)
        => PostAsync<HncSdkIpcResult>("api/hnc-sdk/write", request, ct);
    public Task<HncSdkValueResult<IReadOnlyList<HncSdkFileEntry>>> BrowseFilesAsync(HncSdkBrowseRequest request, CancellationToken ct)
        => PostAsync<HncSdkValueResult<IReadOnlyList<HncSdkFileEntry>>>("api/hnc-sdk/files", request, ct);
    public Task<HncSdkIpcResult> UploadAsync(HncSdkTransferRequest request, CancellationToken ct)
        => PostAsync<HncSdkIpcResult>("api/hnc-sdk/upload", request, ct);
    public Task<HncSdkIpcResult> DownloadAsync(HncSdkTransferRequest request, CancellationToken ct)
        => PostAsync<HncSdkIpcResult>("api/hnc-sdk/download", request, ct);
    public Task<HncSdkIpcResult> RemoveAsync(HncSdkRemoveRequest request, CancellationToken ct)
        => PostAsync<HncSdkIpcResult>("api/hnc-sdk/remove", request, ct);
    public Task<HncSdkIpcResult> RenameAsync(HncSdkRenameRequest request, CancellationToken ct)
        => PostAsync<HncSdkIpcResult>("api/hnc-sdk/rename", request, ct);

    private async Task<T> PostAsync<T>(string path, object request, CancellationToken ct)
        where T : HncSdkIpcResult, new()
    {
        using var response = await httpClient.PostAsJsonAsync(path, request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
            ?? new T { ReturnCode = -1, ErrorMessage = "Empty HNC SDK shim response" };
    }
}
