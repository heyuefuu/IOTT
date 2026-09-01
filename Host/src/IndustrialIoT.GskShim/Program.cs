using IndustrialIoT.Protocols.Gsk;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:39123");
builder.Services.AddSingleton<IGskrmApi, NativeGskrmApi>();

var app = builder.Build();
var group = app.MapGroup("/api/gskrm");

group.MapPost("/create-instance", (GskrmCreateInstanceRequest request, IGskrmApi api) =>
{
    int rc = api.CreateInstance(request.Host, request.Port, request.TimeoutMs, out int handle);
    return new GskrmCreateInstanceResult { ReturnCode = rc, Handle = handle };
});
group.MapPost("/close-instance", (GskrmHandleRequest request, IGskrmApi api) =>
    Code(api.CloseInstance(request.Handle)));
group.MapPost("/get-connect-state", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetConnectState(request.Handle, out bool value), value));
group.MapPost("/set-overtime", (GskrmTimeoutRequest request, IGskrmApi api) =>
    Code(api.SetOvertime(request.Handle, request.TimeoutMs)));

group.MapPost("/get-cnc-info", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetCncInfo(request.Handle, out GskrmCncInfo value), value));
group.MapPost("/get-cnc-type-name", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetCncTypeName(request.Handle, out string value), value));
group.MapPost("/get-avail-axis-count", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetAvailAxisCount(request.Handle, out int value), value));
group.MapPost("/get-avail-axis-name", (GskrmAxisRequest request, IGskrmApi api) =>
    Value(api.GetAvailAxisName(request.Handle, request.AxisIndex, out string value), value));
group.MapPost("/get-avail-axis-units", (GskrmAxisRequest request, IGskrmApi api) =>
    Value(api.GetAvailAxisUnits(request.Handle, request.AxisIndex, out string value), value));

group.MapPost("/get-cnc-state", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetCncState(request.Handle, out GskrmCncState value), value));
group.MapPost("/get-status", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetStatus(request.Handle, out int value), value));
group.MapPost("/get-work-mode", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetWorkMode(request.Handle, out string value), value));
group.MapPost("/get-run-cnc-prog-name", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetRunCncProgName(request.Handle, out string value), value));
group.MapPost("/get-main-cnc-prog-name", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetMainCncProgName(request.Handle, out string value), value));
group.MapPost("/get-run-line-no", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetRunLineNo(request.Handle, out int value), value));
group.MapPost("/get-esp-state", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetEspState(request.Handle, out bool value), value));

group.MapPost("/get-position", (GskrmAxisRequest request, IGskrmApi api) =>
    Value(api.GetPosition(request.Handle, request.AxisIndex, out GskrmPosition value), value));
group.MapPost("/get-fec-point", (GskrmAxisRequest request, IGskrmApi api) =>
    Value(api.GetFecPoint(request.Handle, request.AxisIndex, out double value), value));

group.MapPost("/get-feed-speed-act", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetFeedSpeedAct(request.Handle, out int value), value));
group.MapPost("/get-feed-speed-prog", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetFeedSpeedProg(request.Handle, out int value), value));
group.MapPost("/get-spindle-speed-act", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetSpindleSpeedAct(request.Handle, out int value), value));
group.MapPost("/get-spindle-speed-prog", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetSpindleSpeedProg(request.Handle, out int value), value));
group.MapPost("/get-all-rate-info", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetAllRateInfo(request.Handle, out GskrmRateInfo value), value));

group.MapPost("/get-alarm-count", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetAlarmCount(request.Handle, out int value), value));
group.MapPost("/get-alarm-info", (GskrmIndexedRequest request, IGskrmApi api) =>
    Value(api.GetAlarmInfo(request.Handle, request.Index, out GskrmAlarm value), value));

group.MapPost("/get-plc-data", (GskrmPlcReadRequest request, IGskrmApi api) =>
{
    var buffer = new byte[request.Length];
    int rc = api.GetPlcData(request.Handle, request.Address, request.Length, buffer);
    return Value(rc, buffer);
});
group.MapPost("/set-plc-data", (GskrmPlcWriteRequest request, IGskrmApi api) =>
    Code(api.SetPlcData(request.Handle, request.Address, request.Data)));

group.MapPost("/get-macro-value", (GskrmNumberRequest request, IGskrmApi api) =>
    Value(api.GetMacroValue(request.Handle, request.Number, out double value), value));
group.MapPost("/set-macro-value", (GskrmMacroWriteRequest request, IGskrmApi api) =>
    Code(api.SetMacroValue(request.Handle, request.Number, request.Value)));
group.MapPost("/get-param-value", (GskrmParamRequest request, IGskrmApi api) =>
    Value(api.GetParamValue(request.Handle, request.Number, request.Axis, out int value), value));
group.MapPost("/set-param-value", (GskrmParamWriteRequest request, IGskrmApi api) =>
    Code(api.SetParamValue(request.Handle, request.Number, request.Axis, request.Value)));

group.MapPost("/get-tool-offset-count", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetToolOffsetCount(request.Handle, out int value), value));
group.MapPost("/get-tool-offset-value", (GskrmIndexedRequest request, IGskrmApi api) =>
    Value(api.GetToolOffsetValue(request.Handle, request.Index, out double value), value));

group.MapPost("/get-part-count", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetPartCount(request.Handle, out int value), value));
group.MapPost("/get-cut-time", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetCutTime(request.Handle, out TimeSpan value), value.TotalSeconds));
group.MapPost("/get-run-time", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetRunTime(request.Handle, out TimeSpan value), value.TotalSeconds));
group.MapPost("/get-cnc-file-count", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetCNCFileCount(request.Handle, out int value), value));
group.MapPost("/get-cnc-file-list", (GskrmHandleRequest request, IGskrmApi api) =>
    Value(api.GetCNCFileList(request.Handle, out IReadOnlyList<GskrmCncFileEntry> value), value));
group.MapPost("/get-cnc-file-info", (GskrmFileInfoRequest request, IGskrmApi api) =>
    Value(api.GetCNCFileInfo(request.Handle, request.Name, out GskrmCncFileEntry value), value));
group.MapPost("/receive-cnc-file", (GskrmReceiveFileRequest request, IGskrmApi api) =>
    Code(api.ReceiveCNCFile(request.Handle, request.RemoteName, request.LocalPath)));
group.MapPost("/send-cnc-file", (GskrmSendFileRequest request, IGskrmApi api) =>
    Code(api.SendCNCFile(request.Handle, request.LocalPath, request.RemoteName)));
group.MapPost("/delete-cnc-file", (GskrmFileNameRequest request, IGskrmApi api) =>
    Code(api.DeleteCNCFile(request.Handle, request.RemoteName)));
group.MapPost("/prog-install", (GskrmFileNameRequest request, IGskrmApi api) =>
    Code(api.ProgInstall(request.Handle, request.RemoteName)));
group.MapPost("/prog-uninstall", (GskrmFileNameRequest request, IGskrmApi api) =>
    Code(api.ProgUninstall(request.Handle, request.RemoteName)));

app.Run();

static GskrmIpcResult Code(int rc) => new() { ReturnCode = rc };

static GskrmValueResult<T> Value<T>(int rc, T value) => new() { ReturnCode = rc, Value = value };
