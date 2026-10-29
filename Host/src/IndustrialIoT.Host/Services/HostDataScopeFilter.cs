namespace IndustrialIoT.Host.Services;

using System.Text.Json;
using IndustrialIoT.Application.DTOs;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Protocols.Pipeline;
using IndustrialIoT.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

public sealed partial class HostDataScopeFilter(ICollectionProfileRepository profiles, INCProgramRepository programs,
    ICollectionPipeline pipeline, IBatchProgramTransferTaskService transfers, IDeviceRepository? devices = null) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.Items[HostAccessValidator.TrustedKey] is true) { await next(); return; }
        if (context.HttpContext.Items[HostAccessValidator.GrantKey] is not HostAccessGrant grant)
        { context.Result = new UnauthorizedResult(); return; }
        var action = (ControllerActionDescriptor)context.ActionDescriptor;
        if (!PermissionAllowed(action.ControllerName, action.ActionName, grant)
            || !await ScopeAllowedAsync(action.ControllerName, action.ActionName, context.ActionArguments,
                grant.Scope, context.HttpContext.RequestAborted))
        {
            context.Result = new ObjectResult(new { error = "当前账号无权访问该设备或执行此操作。" }) { StatusCode = 403 };
            return;
        }
        var executed = await next();
        if (grant.Scope.Mode != "all" && executed.Result is ObjectResult result)
            result.Value = result.Value switch
            {
                IEnumerable<DeviceDto> devices => devices.Where(device => grant.Scope.Allows(device.Id)).ToList(),
                IReadOnlyDictionary<string, CollectionTaskStatus> rows => rows.Where(pair => grant.Scope.Allows(pair.Value.DeviceId))
                    .ToDictionary(pair => pair.Key, pair => pair.Value),
                _ => result.Value,
            };
    }

    public static bool PermissionAllowed(string controller, string action, HostAccessGrant grant)
    {
        string[] required = (controller, action) switch
        {
            ("Devices", "GetAll" or "GetById") => ["device_manage", "data_read", "data_write", "report_manage"],
            ("Devices", _) or ("CollectionConfig" or "BatchImport", _) => ["device_manage"],
            ("Collection", _) => ["device_manage"],
            ("AddressSpace" or "NCLink" or "PlcCapabilities", _) => ["data_read", "device_manage"],
            ("DataReadWrite", "WriteTags") => ["data_write", "device_manage"],
            ("DataReadWrite", _) => ["data_read", "device_manage", "report_manage"],
            ("ProgramTransfer", "Upload" or "ResumeUpload" or "UploadBatch") => ["data_write", "device_manage"],
            ("VerificationTransfer", "RoundTrip") => ["data_write", "device_manage"],
            ("ProgramTransfer" or "VerificationTransfer", _) => ["data_read", "device_manage", "report_manage"],
            ("RuntimeLogs", _) => ["log_manage"],
            ("LocalHistory", _) => ["data_read", "device_manage", "report_manage"],
            ("ConnectionVerification", _) => ["report_manage"],
            _ => ["system_manage"],
        };
        return required.Any(grant.HasPermission);
    }
}
