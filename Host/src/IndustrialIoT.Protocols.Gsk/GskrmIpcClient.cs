namespace IndustrialIoT.Protocols.Gsk;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed partial class GskrmIpcClient : IGskrmApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient httpClient;

    public GskrmIpcClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public GskrmIpcClient(Uri baseAddress)
        : this(new HttpClient { BaseAddress = baseAddress })
    {
    }

    private T Post<T>(string path, object request) where T : GskrmIpcResult, new()
    {
        using var response = httpClient.PostAsJsonAsync(path, request, JsonOptions).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return response.Content.ReadFromJsonAsync<T>(JsonOptions).GetAwaiter().GetResult()
            ?? new T { ReturnCode = GskrmErrorCodes.IoError };
    }

    private int PostCode(string path, object request)
        => Post<GskrmIpcResult>(path, request).ReturnCode;

    private int PostValue<T>(string path, object request, out T? value)
    {
        var result = Post<GskrmValueResult<T>>(path, request);
        value = result.Value;
        return result.ReturnCode;
    }

    private int PostInt(string path, object request, out int value)
    {
        int rc = PostValue<int>(path, request, out var result);
        value = result;
        return rc;
    }

    private int PostString(string path, object request, out string value)
    {
        int rc = PostValue<string>(path, request, out var result);
        value = result ?? "";
        return rc;
    }

    public int CreateInstance(string host, int port, int timeoutMs, out int handle)
    {
        var result = Post<GskrmCreateInstanceResult>("api/gskrm/create-instance",
            new GskrmCreateInstanceRequest(host, port, timeoutMs));
        handle = result.Handle;
        return result.ReturnCode;
    }
}
