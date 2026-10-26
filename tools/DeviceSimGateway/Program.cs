using DeviceSimGateway.Core;
using DeviceSimGateway.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:8090");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// 协议服务清单：已实现的在前，其余为扩展占位。
var services = new List<IDeviceService>
{
    new ModbusTcpService(),
    new MtconnectService(),
    new SiemensS7Service(),
    new OpcUaService(),
    new GskWebServerService(),
    new FinsService(),
    new NcLinkService(),
    new FtpServerService(),
    new NcLinkApiService(),
    new MewtocolService(),
    new HaasMdcService(),
    new JingDiaoShimService(),
};

var configPath = Path.Combine(AppContext.BaseDirectory, "sim-config.json");
builder.Services.AddSingleton(sp => new ServiceRegistry(services, configPath, sp.GetRequiredService<ILogger<ServiceRegistry>>(), sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton(services);

var app = builder.Build();
var registry = app.Services.GetRequiredService<ServiceRegistry>();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (ServiceRegistry reg) => Results.Ok(new
{
    app = "DeviceSimGateway",
    time = DateTimeOffset.Now,
    services = reg.GetInfos(),
}));

app.MapGet("/api/services", (ServiceRegistry reg) => Results.Ok(reg.GetInfos()));

app.MapPost("/api/services/{key}/start", async (string key, ServiceRegistry reg, CancellationToken ct) =>
    Results.Ok(new { message = await reg.StartAsync(key, ct) }));

app.MapPost("/api/services/{key}/stop", async (string key, ServiceRegistry reg, CancellationToken ct) =>
    Results.Ok(new { message = await reg.StopAsync(key, ct) }));

app.MapPut("/api/services/{key}", async (string key, ServiceUpdateDto dto, ServiceRegistry reg, CancellationToken ct) =>
    Results.Ok(new { message = await reg.SetConfigAsync(key, dto.Port, dto.Enabled, ct) }));

app.MapGet("/api/data", (int? count, ServiceRegistry reg) => Results.Ok(reg.Store.Snapshot(count ?? 32)));

app.MapGet("/api/generator", (ServiceRegistry reg) => Results.Ok(reg.Generator.Config));

app.MapPut("/api/generator", (GeneratorConfig cfg, ServiceRegistry reg) =>
{
    reg.SetGenerator(cfg);
    return Results.Ok(new { message = "模拟数据生成配置已应用", config = reg.Generator.Config });
});

// 随机失败注入：让协议服务按概率返回错误/坏质量或直接断链，便于联调稳定性与中断处理。
app.MapGet("/api/failure", (ServiceRegistry reg) =>
{
    var s = reg.Store.Failure.Snapshot();
    return Results.Ok(new { enabled = s.Enabled, ratePercent = s.RatePercent, dropConnection = s.DropConnection });
});

app.MapPut("/api/failure", (FailureUpdateDto dto, ServiceRegistry reg) =>
{
    reg.SetFailure(dto.Enabled, dto.RatePercent, dto.DropConnection);
    var s = reg.Store.Failure.Snapshot();
    return Results.Ok(new { message = "随机失败率已应用", enabled = s.Enabled, ratePercent = s.RatePercent, dropConnection = s.DropConnection });
});

// 虚拟第三方应用（MQTT 订阅端）：验证第三方按《第三方数据同步接口文档》订阅我方出站主题的连通性与契约一致性。
app.MapGet("/api/thirdparty/status", (ServiceRegistry reg) => Results.Ok(reg.ThirdParty.GetStatus()));

app.MapPut("/api/thirdparty", (ThirdPartySubscriberConfig cfg, ServiceRegistry reg) =>
{
    reg.SetThirdParty(cfg);
    return Results.Ok(new { message = "第三方订阅模拟配置已应用", status = reg.ThirdParty.GetStatus() });
});

app.MapGet("/api/doc", (ServiceRegistry reg) => Results.Content(BuildPortDoc(reg), "text/markdown; charset=utf-8"));
app.MapGet("/api/ports", (ServiceRegistry reg) => Results.Ok(reg.GetInfos().Select(s => new { s.Key, s.Name, s.Port, s.Enabled, s.State })));

await registry.StartAllAsync(CancellationToken.None);
registry.ThirdParty.Start(app.Lifetime.ApplicationStopping);
app.Lifetime.ApplicationStopping.Register(() =>
{
    try { registry.ThirdParty.Stop(); } catch { }
    try { registry.StopAllAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
});

app.Logger.LogInformation("模拟设备网关管理台: http://0.0.0.0:{Port}/", builder.Configuration["Urls"]);
app.Logger.LogInformation("端口映射：{Map}", string.Join("  ", registry.GetInfos().Select(s => $"{s.Key}={s.Port}[{s.State}]")));

string BuildPortDoc(ServiceRegistry reg)
{
    var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["modbus"] = "Modbus/TCP 从站；FC 01/02/03/04/05/06/0F/10；HR 可读写、IR/离散由生成器驱动、线圈可读写；地址示例 HR0 / IR0 / C0",
        ["mtconnect"] = "MTConnect 1.3 只读 Agent；GET /probe、/current",
        ["s7"] = "S7comm/ISO-TSAP；DB/M/I/Q，BIT/BYTE/WORD/DWORD/REAL；地址示例 DB1.DBW0",
        ["opcua"] = "匿名/None 端点 opc.tcp://<ip>:<port>/DeviceSimGateway；变量 ns=1;s=Sim.X/Y/Z/Spindle/Feed/PartCount",
        ["gskws"] = "HTTP /api/v1/cnc/mc|params|macro-values|info/diags|info/alarms|mc/gcode + WS /ws/cnc",
        ["fins"] = "FINS/TCP；命令 0101/0102；区 DM/CIO/WR/HR/AR（字/位）；地址示例 DM100",
        ["ncLink"] = "内置 MQTT Broker；设备 guid=sim-device-001；topic Query/Set/Probe/Version/Register + Sample/{guid}/ch1",
        ["ftp"] = "FTP 服务端；程序库根 /（含 O0001.CNC/O0002.CNC/PROGRAMS/O0003.CNC）；支持 LIST/NLST/MLSD/RETR/STOR/DELE/SIZE/MKD/RMD",
        ["ncLinkApi"] = "华中 NCLinkApi(HTTP)；POST /v1/{deviceId}/data/（get_value/get_keys/set_value…，响应 value=[[..]]）、GET|POST /v1/{deviceId}/file/、GET /api/tools/check-device",
        ["mewtocol"] = "Panasonic Mewtocol（TCP ASCII）：%站号[#]命令(3)+数据+BCC(2)+CR；RDD/RDS 读字、RCC 读位、WDD/WCC 写",
        ["haas"] = "Haas MDC ASCII（TCP 9999）：Q 命令读（Q100/Q104/Q500…）、Q600 NNNNN 读宏、E NNNNN value 写宏；响应 >Qxxx VALUE / MACRO,N,value",
        ["jingdiao"] = "北京精雕 shim mock（HTTP /api/jingdiao/*，端口 7080）：Host 精雕驱动设 ShimBaseUri=http://<ip>:7080 即可联调",
    };
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("# DeviceSimGateway 端口与协议一览");
    sb.AppendLine();
    sb.AppendLine($"生成时间：{DateTimeOffset.Now:o}");
    sb.AppendLine();
    sb.AppendLine("| Key | 协议 | 端口 | 启用 | 状态 | 说明 |");
    sb.AppendLine("|---|---|---|---|---|---|");
    foreach (var s in reg.GetInfos())
        sb.AppendLine($"| {s.Key} | {s.Name} | {s.Port} | {s.Enabled} | {s.State} | {s.Description} |");
    sb.AppendLine();
    sb.AppendLine("## 端点 / 地址示例");
    foreach (var s in reg.GetInfos())
        if (notes.TryGetValue(s.Key, out var n)) sb.AppendLine($"- **{s.Key}**（{s.Port}）：{n}");
    sb.AppendLine();
    sb.AppendLine("> 管理台 http://<本机IP>:8090/ 可开关服务、改端口；配置持久化于 sim-config.json。");
    return sb.ToString();
}

app.Run();

public sealed record ServiceUpdateDto(int? Port, bool? Enabled);
public sealed record FailureUpdateDto(bool Enabled, double RatePercent, bool DropConnection);
