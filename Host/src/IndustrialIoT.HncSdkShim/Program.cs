using IndustrialIoT.HncSdkShim;
using IndustrialIoT.Protocols.HncSdk;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? HncSdkShimProcess.DefaultBaseUrl);
builder.Services.AddSingleton<IHncSdkGateway, ReflectionHncSdkGateway>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var group = app.MapGroup("/api/hnc-sdk");

group.MapPost("/connect", async (HncSdkConnectRequest request, IHncSdkGateway gateway, CancellationToken ct) =>
    await gateway.ConnectAsync(request, ct));
group.MapPost("/disconnect", async (HncSdkSessionRequest request, IHncSdkGateway gateway, CancellationToken ct) =>
    await gateway.DisconnectAsync(request.SessionId, ct));
group.MapPost("/ping", async (HncSdkSessionRequest request, IHncSdkGateway gateway, CancellationToken ct) =>
    await gateway.PingAsync(request.SessionId, ct));
group.MapPost("/read", async (HncSdkReadRequest request, IHncSdkGateway gateway, CancellationToken ct) =>
    await gateway.ReadAsync(request, ct));
group.MapPost("/write", async (HncSdkWriteRequest request, IHncSdkGateway gateway, CancellationToken ct) =>
    await gateway.WriteAsync(request, ct));
group.MapPost("/files", async (HncSdkBrowseRequest request, IHncSdkGateway gateway, CancellationToken ct) =>
    await gateway.BrowseFilesAsync(request, ct));
group.MapPost("/upload", async (HncSdkTransferRequest request, IHncSdkGateway gateway, CancellationToken ct) =>
    await gateway.UploadAsync(request, ct));
group.MapPost("/download", async (HncSdkTransferRequest request, IHncSdkGateway gateway, CancellationToken ct) =>
    await gateway.DownloadAsync(request, ct));
group.MapPost("/remove", async (HncSdkRemoveRequest request, IHncSdkGateway gateway, CancellationToken ct) =>
    await gateway.RemoveAsync(request, ct));
group.MapPost("/rename", async (HncSdkRenameRequest request, IHncSdkGateway gateway, CancellationToken ct) =>
    await gateway.RenameAsync(request, ct));

app.Run();
