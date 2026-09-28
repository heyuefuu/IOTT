using MachineConnectionApi.Data;
using MachineConnectionApi.Options;
using MachineConnectionApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args);
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
    options.UseMySql(builder.Configuration.GetConnectionString("MachineCollection"),
        new MySqlServerVersion(Version.Parse(builder.Configuration["Database:ServerVersion"] ?? "8.4.0"))));

builder.Services.Configure<InfluxDbOptions>(
    builder.Configuration.GetSection(InfluxDbOptions.SectionName));
builder.Services.AddSingleton<InfluxSettingsStore>();
builder.Services.AddSingleton<IPostConfigureOptions<InfluxDbOptions>>(sp => sp.GetRequiredService<InfluxSettingsStore>());
builder.Services.AddSingleton<InfluxConnectionTester>();
builder.Services.Configure<MqttOptions>(
    builder.Configuration.GetSection(MqttOptions.SectionName));
builder.Services.AddSingleton<IInfluxTelemetryWriter, InfluxTelemetryWriter>();
builder.Services.AddSingleton<IMqttTelemetryPublisher, MqttTelemetryPublisher>();
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
builder.Services.AddSingleton<EvaluationIndicatorStore>();
builder.Services.AddSingleton<EvaluationKnowledgeStore>();
builder.Services.AddSingleton<IVerifyTaskStore, VerifyTaskStore>();
builder.Services.AddSingleton<VerifyExecutionLeaseService>();
builder.Services.AddSingleton<VerifyTaskCompletionJournal>();
builder.Services.AddSingleton<IVerifyTaskRunner, VerifyTaskRunner>();
builder.Services.AddHostedService<VerifyTaskSchedulerHostedService>();

builder.Services.AddScoped<BusinessApiAuthorizationFilter>();
builder.Services.AddControllers(options => options.Filters.AddService<BusinessApiAuthorizationFilter>());
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

using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<MCConfigurationDbContext>().Database;
    await database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthorization();

// 确保存在可登录账号（首次启动创建默认管理员 admin / admin@123）
app.Services.GetRequiredService<IAuthService>().EnsureSeeded();

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
