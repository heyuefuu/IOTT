namespace IndustrialIoT.Protocols.JingDiao;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

public interface IJingDiaoClient
{
    Task<JingDiaoConnectResult> ConnectAsync(JingDiaoConnectRequest request, CancellationToken ct);
    Task<JingDiaoIpcResult> DisconnectAsync(string sessionId, CancellationToken ct);
    Task<JingDiaoIpcResult> PingAsync(string sessionId, CancellationToken ct);
    Task<JingDiaoValueResult<JingDiaoPositionSnapshot>> GetMachPosAsync(string sessionId, CancellationToken ct);
    Task<JingDiaoValueResult<JingDiaoModalSnapshot>> GetBasicModalAsync(string sessionId, CancellationToken ct);
    Task<JingDiaoValueResult<int>> GetProgStateAsync(string sessionId, CancellationToken ct);
    Task<JingDiaoValueResult<int>> GetAlarmAsync(string sessionId, CancellationToken ct);
    Task<JingDiaoValueResult<JingDiaoSpindleSnapshot>> GetSpindleAsync(string sessionId, CancellationToken ct);
    Task<JingDiaoValueResult<JingDiaoRateSnapshot>> GetRateAsync(string sessionId, CancellationToken ct);
    Task<JingDiaoValueResult<double>> GetMacroAsync(string sessionId, int number, CancellationToken ct);
    Task<JingDiaoValueResult<int>> GetLineNoAsync(string sessionId, CancellationToken ct);
    Task<JingDiaoValueResult<int>> GetPartCountAsync(string sessionId, CancellationToken ct);
    Task<JingDiaoValueResult<IReadOnlyList<JingDiaoFileEntry>>> BrowseFilesAsync(JingDiaoBrowseFilesRequest request, CancellationToken ct);
    Task<JingDiaoIpcResult> UploadAsync(string sessionId, Stream source, string fileName, bool addToTask, bool setMainProgram, CancellationToken ct);
    Task<Stream> DownloadAsync(JingDiaoDownloadRequest request, CancellationToken ct);
}

public sealed class JingDiaoIpcClient : IJingDiaoClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient httpClient;

    public JingDiaoIpcClient(HttpClient httpClient) => this.httpClient = httpClient;
    public JingDiaoIpcClient(Uri baseAddress) : this(new HttpClient { BaseAddress = baseAddress }) { }

    public Task<JingDiaoConnectResult> ConnectAsync(JingDiaoConnectRequest request, CancellationToken ct)
        => PostAsync<JingDiaoConnectResult>("api/jingdiao/connect", request, ct);
    public Task<JingDiaoIpcResult> DisconnectAsync(string sessionId, CancellationToken ct)
        => PostAsync<JingDiaoIpcResult>("api/jingdiao/disconnect", new JingDiaoSessionRequest(sessionId), ct);
    public Task<JingDiaoIpcResult> PingAsync(string sessionId, CancellationToken ct)
        => PostAsync<JingDiaoIpcResult>("api/jingdiao/ping", new JingDiaoSessionRequest(sessionId), ct);
    public Task<JingDiaoValueResult<JingDiaoPositionSnapshot>> GetMachPosAsync(string sessionId, CancellationToken ct)
        => PostAsync<JingDiaoValueResult<JingDiaoPositionSnapshot>>("api/jingdiao/get-mach-pos", new JingDiaoSessionRequest(sessionId), ct);
    public Task<JingDiaoValueResult<JingDiaoModalSnapshot>> GetBasicModalAsync(string sessionId, CancellationToken ct)
        => PostAsync<JingDiaoValueResult<JingDiaoModalSnapshot>>("api/jingdiao/get-basic-modal", new JingDiaoSessionRequest(sessionId), ct);
    public Task<JingDiaoValueResult<int>> GetProgStateAsync(string sessionId, CancellationToken ct)
        => PostAsync<JingDiaoValueResult<int>>("api/jingdiao/get-prog-state", new JingDiaoSessionRequest(sessionId), ct);
    public Task<JingDiaoValueResult<int>> GetAlarmAsync(string sessionId, CancellationToken ct)
        => PostAsync<JingDiaoValueResult<int>>("api/jingdiao/get-alarm", new JingDiaoSessionRequest(sessionId), ct);
    public Task<JingDiaoValueResult<JingDiaoSpindleSnapshot>> GetSpindleAsync(string sessionId, CancellationToken ct)
        => PostAsync<JingDiaoValueResult<JingDiaoSpindleSnapshot>>("api/jingdiao/get-spindle", new JingDiaoSessionRequest(sessionId), ct);
    public Task<JingDiaoValueResult<JingDiaoRateSnapshot>> GetRateAsync(string sessionId, CancellationToken ct)
        => PostAsync<JingDiaoValueResult<JingDiaoRateSnapshot>>("api/jingdiao/get-rate", new JingDiaoSessionRequest(sessionId), ct);
    public Task<JingDiaoValueResult<double>> GetMacroAsync(string sessionId, int number, CancellationToken ct)
        => PostAsync<JingDiaoValueResult<double>>("api/jingdiao/get-macro", new JingDiaoMacroRequest(sessionId, number), ct);
    public Task<JingDiaoValueResult<int>> GetLineNoAsync(string sessionId, CancellationToken ct)
        => PostAsync<JingDiaoValueResult<int>>("api/jingdiao/get-line-no", new JingDiaoSessionRequest(sessionId), ct);
    public Task<JingDiaoValueResult<int>> GetPartCountAsync(string sessionId, CancellationToken ct)
        => PostAsync<JingDiaoValueResult<int>>("api/jingdiao/get-part-count", new JingDiaoSessionRequest(sessionId), ct);
    public Task<JingDiaoValueResult<IReadOnlyList<JingDiaoFileEntry>>> BrowseFilesAsync(JingDiaoBrowseFilesRequest request, CancellationToken ct)
        => PostAsync<JingDiaoValueResult<IReadOnlyList<JingDiaoFileEntry>>>("api/jingdiao/list-files", request, ct);

    public async Task<JingDiaoIpcResult> UploadAsync(
        string sessionId, Stream source, string fileName, bool addToTask, bool setMainProgram, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent(sessionId), "sessionId" },
            { new StringContent(addToTask.ToString()), "addToTask" },
            { new StringContent(setMainProgram.ToString()), "setMainProgram" },
        };
        content.Add(new StreamContent(source), "file", fileName);
        using var response = await httpClient.PostAsync("api/jingdiao/send-nc-file", content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JingDiaoIpcResult>(JsonOptions, ct)
            ?? new JingDiaoIpcResult { ReturnCode = -1, ErrorMessage = "Empty JingDiao upload response" };
    }

    public async Task<Stream> DownloadAsync(JingDiaoDownloadRequest request, CancellationToken ct)
    {
        using var response = await httpClient.PostAsJsonAsync("api/jingdiao/receive-file", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        var stream = new MemoryStream();
        await response.Content.CopyToAsync(stream, ct);
        stream.Position = 0;
        return stream;
    }

    private async Task<T> PostAsync<T>(string path, object request, CancellationToken ct)
        where T : JingDiaoIpcResult, new()
    {
        using var response = await httpClient.PostAsJsonAsync(path, request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
            ?? new T { ReturnCode = -1, ErrorMessage = "Empty JingDiao shim response" };
    }
}
