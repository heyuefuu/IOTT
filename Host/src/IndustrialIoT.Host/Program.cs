using IndustrialIoT.Application;
using IndustrialIoT.Infrastructure;
using IndustrialIoT.Infrastructure.Messaging;
using IndustrialIoT.Infrastructure.Persistence;
using IndustrialIoT.Host.Hubs;
using IndustrialIoT.Host.Services;
using IndustrialIoT.Protocols.Registration;
using IndustrialIoT.Protocols.Modbus;
using IndustrialIoT.Protocols.FINS;
using IndustrialIoT.Protocols.Mewtocol;
using IndustrialIoT.Protocols.NCLink;
using IndustrialIoT.Protocols.FANUC;
using IndustrialIoT.Protocols.FileTransfer;
using Serilog;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

var builder = WebApplication.CreateBuilder(args);

// 注册 CodePages 编码提供程序（GBK/GB2312/Big5 等），FANUC 中文报警 / 华中机床 中文程序文件需要
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

var logFilePath = Path.Combine(AppContext.BaseDirectory, "logs", "industrial-iot-.log");
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        logFilePath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true,
        flushToDiskInterval: TimeSpan.FromSeconds(1))
    .CreateLogger();

AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
    Log.Fatal(eventArgs.ExceptionObject as Exception, "Unhandled exception caused Host process termination");

TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
{
    Log.Error(eventArgs.Exception, "Unobserved task exception");
    eventArgs.SetObserved();
};

// Serilog
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .WriteTo.Console()
    .WriteTo.File(
        logFilePath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true,
        flushToDiskInterval: TimeSpan.FromSeconds(1))
    .Enrich.FromLogContext());

// Application layer (MediatR + FluentValidation)
builder.Services.AddApplication();

// Infrastructure (EF Core + Repositories + Background Services)
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Server=(localdb)\\mssqllocaldb;Database=IndustrialIoT;Trusted_Connection=true;";
builder.Services.AddInfrastructure(connectionString, builder.Configuration);

// Protocol driver registry + factory
var registry = new DriverRegistry();
builder.Services.AddSingleton<IDriverRegistry>(registry);
builder.Services.AddSingleton<IProtocolDriverFactory, ProtocolDriverFactory>();
IndustrialIoT.Host.ProtocolDriverRegistration.RegisterRealDrivers(builder.Services, registry);

// API
builder.Services.AddControllers()
    .AddJsonOptions(opts =>
    {
        opts.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Industrial IoT Service API", Version = "v1" });
});

// SignalR — 与 MVC 一致，枚举序列化为字符串（Good/Float…），避免前端 quality === 'Good' 失效
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

// SignalR data output — bridges collection pipeline to real-time push
builder.Services.AddSingleton<SignalRDataOutput>();
builder.Services.AddSingleton<IDataOutput>(sp => sp.GetRequiredService<SignalRDataOutput>());
builder.Services.AddSingleton<IAddressSpaceBrowseService, AddressSpaceBrowseService>();
builder.Services.AddSingleton<IProgramTransferFileBrowserService, ProgramTransferFileBrowserService>();
builder.Services.AddSingleton<IBatchProgramTransferTaskService, BatchProgramTransferTaskService>();

// Manual read/write/browse endpoints borrow the pooled long-lived driver instead of
// connecting and closing per request (which real controllers log as a connection fault)
builder.Services.AddSingleton<IPooledDriverAccessor, PooledDriverAccessor>();

// CORS (allow Vue frontend)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IoTDbContext>();
    await DatabaseBootstrapper.InitializeAsync(db);
}

// Middleware
app.UseSerilogRequestLogging();
app.UseCors();

// A client that gives up mid-request cancels the request token, which surfaces as
// OperationCanceledException. That is not a server fault — log it as a warning and answer
// 499 instead of letting it become a 500 with a full stack trace.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        Log.Warning(
            "Request {Method} {Path} was aborted by the client",
            context.Request.Method, context.Request.Path);

        if (!context.Response.HasStarted)
            context.Response.StatusCode = 499; // client closed request
    }
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Industrial IoT API v1"));
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();
app.MapHub<DeviceDataHub>("/hubs/device-data");

try
{
    Log.Information("IndustrialIoT Host starting. BaseDirectory={BaseDirectory}", AppContext.BaseDirectory);
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host process terminating");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
