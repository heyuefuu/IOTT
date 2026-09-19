namespace MachineConnectionApi.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

public sealed class BusinessApiAuthorizationFilter(IAuthService auth) : IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
            return;

        var session = auth.Validate(context.HttpContext.Request.Headers["X-Auth-Token"].FirstOrDefault());
        if (session is null)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Login required or session expired" });
            return;
        }

        var permissions = context.ActionDescriptor is ControllerActionDescriptor action
            ? RequiredPermissions(action.ControllerName, action.ActionName)
            : ["system_manage"];
        if (permissions.Length > 0 && !permissions.Any(session.HasPermission))
            context.Result = new ObjectResult(new { error = "Permission denied" })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
    }

    private static string[] RequiredPermissions(string controller, string action) => (controller, action) switch
    {
        ("SystemAuth", "Me" or "Logout") or ("SystemStatus", "Get") => [],
        ("SystemUsers", "ListPermissions") => [],
        ("SystemUsers", _) => ["permission_manage"],
        ("SystemLogs", _) => ["log_manage"],
        ("Verify" or "VerifyTasks" or "Metrics" or "ReportTemplates" or "ConnectionVerification", _) => ["report_manage"],
        ("Cs", "ParallelTest" or "ParallelTestReport") => ["config_manage", "report_manage"],
        ("Cs", _) => ["config_manage"],
        ("Devices", "List" or "GetById") => ["device_manage", "data_read", "data_write", "report_manage"],
        ("Devices" or "Collection" or "PlcCapabilities", _) => ["device_manage"],
        ("DataReadWrite", "WriteTags") => ["data_write", "device_manage"],
        ("DataReadWrite" or "AddressSpace" or "NCLinkDiagnostics", _) => ["data_read", "device_manage"],
        ("Datacollection", "Sync") => ["device_manage"],
        ("Datacollection", _) => ["data_read", "device_manage"],
        ("TelemetryInflux", "WriteBatch") => ["data_write", "device_manage"],
        ("TelemetryInflux", "QueryHistory") => ["data_read", "device_manage", "report_manage"],
        ("ProgramTransfer", "Upload" or "UploadBatch" or "Resume") => ["data_write", "device_manage"],
        ("ProgramTransfer", _) => ["data_read", "device_manage"],
        _ => ["system_manage"],
    };
}
