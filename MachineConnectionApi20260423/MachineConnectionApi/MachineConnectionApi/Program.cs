using MachineConnectionApi.Data;
using MachineConnectionApi.Options;
using MachineConnectionApi.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.Logger(lc => lc
            .Filter.ByIncludingOnly(IsMqttLogEvent)
            .WriteTo.File(
                path: "logs/mqtt-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                restrictedToMinimumLevel: LogEventLevel.Information,
                shared: true,
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}"));
});

builder.Services.AddDbContext<MCConfigurationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("MachineCollection")));

builder.Services.Configure<InfluxDbOptions>(
    builder.Configuration.GetSection(InfluxDbOptions.SectionName));
builder.Services.Configure<MqttOptions>(
    builder.Configuration.GetSection(MqttOptions.SectionName));
builder.Services.Configure<NCLinkApiOptions>(
    builder.Configuration.GetSection(NCLinkApiOptions.SectionName));
builder.Services.AddSingleton<IInfluxTelemetryWriter, InfluxTelemetryWriter>();
builder.Services.AddSingleton<IMqttTelemetryPublisher, MqttTelemetryPublisher>();
builder.Services.AddSingleton<INCLinkApiClient, NCLinkApiClient>();
builder.Services.AddSingleton<INCLinkModelProbeClient, NCLinkModelProbeClient>();
builder.Services.AddSingleton<INCLinkMqttQueryClient, NCLinkMqttQueryClient>();
builder.Services.AddSingleton<INCLinkDeviceResolver, NCLinkDeviceResolver>();
builder.Services.AddSingleton<ICsConnectivityService, CsConnectivityService>();
builder.Services.AddSingleton<ICsParallelReportService, CsParallelReportService>();
builder.Services.AddSingleton<IVerifyAutomationService, VerifyAutomationService>();
builder.Services.AddSingleton<IDeviceStore, DeviceStore>();
builder.Services.AddSingleton<IDeviceUpstreamSyncService, DeviceUpstreamSyncService>();
builder.Services.AddHostedService<DeviceUpstreamSyncHostedService>();
builder.Services.AddSingleton<ISystemActivityLog, SystemActivityLog>();
builder.Services.AddSingleton<IUserStore, UserStore>();
builder.Services.AddSingleton<IAuthService, AuthService>();
builder.Services.AddSingleton<IMetricStore, MetricStore>();
builder.Services.AddSingleton<IVerifyTaskStore, VerifyTaskStore>();
builder.Services.AddSingleton<IVerifyTaskRunner, VerifyTaskRunner>();
builder.Services.AddHostedService<VerifyTaskSchedulerHostedService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var industrialBase = builder.Configuration["IndustrialIoT:BaseUrl"] ?? "http://localhost:5173";
if (!industrialBase.EndsWith('/'))
    industrialBase += "/";

builder.Services.AddHttpClient("IndustrialIoT", client =>
{
    client.BaseAddress = new Uri(industrialBase);
    client.Timeout = TimeSpan.FromMinutes(5);
});

var nclinkBase = builder.Configuration["NCLinkApi:BaseUrl"] ?? "http://127.0.0.1:19001";
if (!nclinkBase.EndsWith('/'))
    nclinkBase += "/";
var nclinkTimeoutSec = builder.Configuration.GetValue<int?>("NCLinkApi:TimeoutSeconds") ?? 30;

builder.Services.AddHttpClient(NCLinkApiClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri(nclinkBase);
    client.Timeout = TimeSpan.FromSeconds(Math.Max(5, nclinkTimeoutSec));
});

builder.Services.AddHttpClient(MachineConnectionApi.Controllers.TelemetryInfluxController.InfluxHttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthorization();

// 确保存在可登录账号（首次启动创建默认管理员 admin / admin@123）
app.Services.GetRequiredService<IAuthService>().EnsureSeeded();

// 用户管理接口须携带有效会话且具备 permission_manage 权限（登录/登出/me 除外）
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/api/system/users"))
    {
        var auth = context.RequestServices.GetRequiredService<IAuthService>();
        var session = auth.Validate(context.Request.Headers["X-Auth-Token"].FirstOrDefault());
        if (session is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "未登录或登录已过期" });
            return;
        }
        // 权限清单本身放开（编辑对话框需要），写操作要求权限管理权限
        var readonlyList = context.Request.Method == HttpMethods.Get && path.Value!.EndsWith("/permissions", StringComparison.OrdinalIgnoreCase);
        if (!readonlyList && !session.HasPermission("permission_manage"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "没有权限管理权限" });
            return;
        }
    }
    await next();
});

app.MapControllers();

app.Run();

static bool IsMqttLogEvent(LogEvent evt)
{
    if (evt.Properties.TryGetValue("SourceContext", out var sourceCtx))
    {
        var s = sourceCtx.ToString();
        if (s.Contains("Mqtt", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    var msg = evt.MessageTemplate.Text;
    return msg.Contains("MQTT", StringComparison.OrdinalIgnoreCase);
}
