using IndustrialIoT.JingDiaoShim;
using IndustrialIoT.Protocols.JingDiao;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? JingDiaoShimProcess.DefaultBaseUrl);
builder.Services.AddSingleton<IJdMonApi, NativeJdMonApi>();
builder.Services.AddSingleton<JingDiaoSessionStore>();

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var group = app.MapGroup("/api/jingdiao");

group.MapPost("/connect", (JingDiaoConnectRequest request, IJdMonApi api, JingDiaoSessionStore store) =>
{
    var handle = api.Create();
    if (handle == IntPtr.Zero)
        return Results.Ok(new JingDiaoConnectResult { ReturnCode = -1, ErrorMessage = "CreateJDMachMon returned null." });
    if (request.TimeoutMs > 0)
    {
        api.SetConnectionTimeout(handle, request.TimeoutMs);
        api.SetRpcTimeout(handle, request.TimeoutMs);
    }
    if (!api.Connect(handle, request.Host, request.RpcPort, request.CallbackPort, request.FileUploadPort, request.FileDownloadPort))
    {
        var error = api.GetLastError(handle);
        api.Delete(ref handle);
        return Results.Ok(new JingDiaoConnectResult { ReturnCode = (int)(error == 0 ? 1 : error), ErrorMessage = $"ConnectJDMach failed: {error}" });
    }
    return Results.Ok(new JingDiaoConnectResult { ReturnCode = 0, SessionId = store.Add(handle) });
});

group.MapPost("/disconnect", (JingDiaoSessionRequest request, IJdMonApi api, JingDiaoSessionStore store) =>
{
    if (!store.Remove(request.SessionId, out var handle)) return Results.Ok(Result(-404, "Unknown session."));
    var ok = api.Disconnect(handle);
    api.Delete(ref handle);
    return Results.Ok(ResultFromBool(ok, api, handle));
});

group.MapPost("/ping", (JingDiaoSessionRequest request, IJdMonApi api, JingDiaoSessionStore store) =>
    WithHandle(request.SessionId, store, handle => ResultFromBool(api.IsConnected(handle), api, handle)));

group.MapPost("/get-mach-pos", (JingDiaoSessionRequest request, IJdMonApi api, JingDiaoSessionStore store) =>
    WithHandle(request.SessionId, store, handle =>
    {
        var machine = new double[3];
        var absolute = new double[3];
        var relative = new double[3];
        return Value(api.GetMachPos(handle, machine, absolute, relative), api, handle,
            new JingDiaoPositionSnapshot(machine, absolute, relative));
    }));

group.MapPost("/get-basic-modal", (JingDiaoSessionRequest request, IJdMonApi api, JingDiaoSessionStore store) =>
    WithHandle(request.SessionId, store, handle =>
        Value(api.GetBasicModal(handle, out var value), api, handle, value)));
group.MapPost("/get-prog-state", (JingDiaoSessionRequest request, IJdMonApi api, JingDiaoSessionStore store) =>
    WithHandle(request.SessionId, store, handle => Value(api.GetProgState(handle, out var value), api, handle, value)));
group.MapPost("/get-alarm", (JingDiaoSessionRequest request, IJdMonApi api, JingDiaoSessionStore store) =>
    WithHandle(request.SessionId, store, handle => Value(api.GetAlarm(handle, out var value), api, handle, value)));
group.MapPost("/get-spindle", (JingDiaoSessionRequest request, IJdMonApi api, JingDiaoSessionStore store) =>
    WithHandle(request.SessionId, store, handle =>
    {
        var spindle = new double[3];
        return Value(api.GetSpindle(handle, spindle), api, handle,
            new JingDiaoSpindleSnapshot(spindle[0], spindle[1], spindle[2]));
    }));
group.MapPost("/get-rate", (JingDiaoSessionRequest request, IJdMonApi api, JingDiaoSessionStore store) =>
    WithHandle(request.SessionId, store, handle =>
    {
        var rates = new int[2];
        return Value(api.GetRate(handle, rates), api, handle, new JingDiaoRateSnapshot(rates[0], rates[1]));
    }));
group.MapPost("/get-macro", (JingDiaoMacroRequest request, IJdMonApi api, JingDiaoSessionStore store) =>
    WithHandle(request.SessionId, store, handle =>
        Value(api.GetMacro(handle, request.Number, out var value), api, handle, value)));
group.MapPost("/get-line-no", (JingDiaoSessionRequest request, IJdMonApi api, JingDiaoSessionStore store) =>
    WithHandle(request.SessionId, store, handle => Value(api.GetLineNo(handle, out var value), api, handle, value)));
group.MapPost("/get-part-count", (JingDiaoSessionRequest request, IJdMonApi api, JingDiaoSessionStore store) =>
    WithHandle(request.SessionId, store, handle => Value(api.GetPartCount(handle, out var value), api, handle, value)));

group.MapPost("/list-files", (JingDiaoBrowseFilesRequest request, IJdMonApi api, JingDiaoSessionStore store) =>
    WithHandle(request.SessionId, store, handle =>
    {
        var ok = api.GetMachFileList(handle, request.Path ?? "", 102400, out var fileList);
        return Value(ok, api, handle, ParseFileList(request.Path, fileList));
    }));

group.MapPost("/send-nc-file", async (HttpRequest request, IJdMonApi api, JingDiaoSessionStore store, CancellationToken ct) =>
{
    var form = await request.ReadFormAsync(ct);
    var sessionId = form["sessionId"].ToString();
    if (!store.TryGet(sessionId, out var handle)) return Results.Ok(Result(-404, "Unknown session."));
    var file = form.Files.GetFile("file");
    if (file is null) return Results.Ok(Result(-1, "Missing multipart file field."));
    var addToTask = bool.TryParse(form["addToTask"], out var add) && add;
    var setMainProgram = bool.TryParse(form["setMainProgram"], out var setMain) && setMain;
    var temp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{Path.GetFileName(file.FileName)}");
    try
    {
        await using (var output = File.Create(temp)) await file.CopyToAsync(output, ct);
        return Results.Ok(ResultFromBool(api.SendNcFile(handle, temp, addToTask, setMainProgram), api, handle));
    }
    finally { TryDelete(temp); }
});

group.MapPost("/receive-file", async (JingDiaoDownloadRequest request, IJdMonApi api, JingDiaoSessionStore store, CancellationToken ct) =>
{
    if (!store.TryGet(request.SessionId, out var handle)) return Results.NotFound(Result(-404, "Unknown session."));
    var temp = Path.GetTempFileName();
    try
    {
        if (!api.ReceiveFile(handle, request.RemotePath, temp))
            return Results.BadRequest(ResultFromBool(false, api, handle));
        var bytes = await File.ReadAllBytesAsync(temp, ct);
        return Results.File(bytes, "application/octet-stream", Path.GetFileName(request.RemotePath));
    }
    finally { TryDelete(temp); }
});

group.MapPost("/delete-file", (JingDiaoDeleteFileRequest request, IJdMonApi api, JingDiaoSessionStore store) =>
    WithHandle(request.SessionId, store, handle =>
        ResultFromBool(api.DeleteFile(handle, request.Directory, request.FileName), api, handle)));

app.Run();

static IResult WithHandle(string sessionId, JingDiaoSessionStore store, Func<IntPtr, object> action)
    => store.TryGet(sessionId, out var handle) ? Results.Ok(action(handle)) : Results.Ok(Result(-404, "Unknown session."));

static JingDiaoIpcResult ResultFromBool(bool ok, IJdMonApi api, IntPtr handle)
{
    var error = ok ? 0 : api.GetLastError(handle);
    return Result(ok ? 0 : (int)(error == 0 ? 1 : error), ok ? null : $"NcMonIO call failed: {error}");
}

static JingDiaoIpcResult Result(int returnCode, string? message = null) => new() { ReturnCode = returnCode, ErrorMessage = message };

static JingDiaoValueResult<T> Value<T>(bool ok, IJdMonApi api, IntPtr handle, T value)
{
    var result = ResultFromBool(ok, api, handle);
    return new() { ReturnCode = result.ReturnCode, ErrorMessage = result.ErrorMessage, Value = ok ? value : default };
}

static IReadOnlyList<JingDiaoFileEntry> ParseFileList(string? directory, string fileList)
{
    var basePath = directory ?? "";
    return fileList.Split(["\r\n", "\n", "\r", ";", "|"], StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim())
        .Where(x => x.Length > 0)
        .Select(x =>
        {
            var name = Path.GetFileName(x.TrimEnd('/', '\\'));
            var isDirectory = x.EndsWith('/') || x.EndsWith('\\');
            var path = string.IsNullOrWhiteSpace(basePath) ? x : $"{basePath.TrimEnd('/', '\\')}/{name}";
            return new JingDiaoFileEntry(path, string.IsNullOrWhiteSpace(name) ? x : name, isDirectory, null);
        })
        .ToArray();
}

static void TryDelete(string path)
{
    try { if (File.Exists(path)) File.Delete(path); } catch { }
}
