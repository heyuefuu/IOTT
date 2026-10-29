namespace MachineConnectionApi.Services;

using System.Text.Json;
using IndustrialIoT.Security;
using MachineConnectionApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

public sealed partial class BusinessDataScopeFilter(IVerifyTaskStore tasks, EvaluationKnowledgeStore knowledge,
    IDeviceStore devices) : IAsyncActionFilter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.Items[typeof(AuthSession)] is not AuthSession session) { await next(); return; }
        var action = (ControllerActionDescriptor)context.ActionDescriptor;
        if (!WritePermissionAllowed(action.ControllerName, action.ActionName, context.ActionArguments, session)
            || !CanExecute(action.ControllerName, action.ActionName, context.ActionArguments, session.DataScope))
        {
            context.Result = new ObjectResult(new { error = "当前账号的功能权限或设备数据范围不足。" }) { StatusCode = 403 };
            return;
        }
        var executed = await next();
        if (executed.Result is ObjectResult result)
            result.Value = FilterResult(result.Value, session.DataScope);
    }

    public bool CanExecute(string controller, string action, IDictionary<string, object?> arguments, DeviceDataScope scope)
    {
        if (scope.Mode == "all") return true;
        if (controller is "Cs" or "HostRuntimeLogs" or "SystemLogs" or "DiagnosticsProxy" or "InfluxSettings"
            or "TelemetryMqtt" or "ConnectionVerification") return false;
        if (controller == "Devices") return action switch
        {
            "List" => true,
            "GetById" or "Update" or "Delete" or "TestConnection" => AllowedId(arguments, "id", scope),
            _ => false,
        };
        if (controller == "VerifyTasks") return TaskAction(action, arguments, scope);
        if (controller == "EvaluationKnowledge") return KnowledgeAction(action, arguments, scope);
        if (controller is "Collection" or "ProgramTransfer" or "AddressSpace" or "DataReadWrite")
            return true; // The upstream revalidates this live session and resolves profile/transfer ownership.
        if (controller is "Datacollection" or "TelemetryInflux" or "Verify")
        {
            var ids = ArgumentReferences(arguments).Distinct(StringComparer.Ordinal).ToList();
            return ids.Count > 0 && ids.All(scope.Allows);
        }
        return true;
    }

    public object? FilterResult(object? value, DeviceDataScope scope) => scope.Mode == "all" ? value : value switch
    {
        IEnumerable<MachineDeviceDto> rows => rows.Where(row => scope.Allows(row.Id)).ToList(),
        IEnumerable<VerifyTaskDto> rows => rows.Where(row => CanReadTask(row, scope)).ToList(),
        IEnumerable<KnowledgeRecord> rows => rows.Where(row => CanReadKnowledge(row, scope)).ToList(),
        IEnumerable<KnowledgeTask> rows => rows.Where(row => TaskAllowed(row.Id, scope)).ToList(),
        _ => value,
    };

    private static bool AllowedId(IDictionary<string, object?> arguments, string key, DeviceDataScope scope) =>
        arguments.TryGetValue(key, out var id) && id is string text && scope.Allows(text);

    private static IEnumerable<string> ArgumentReferences(IDictionary<string, object?> arguments)
    {
        foreach (var (name, value) in arguments)
        {
            if (value is null or CancellationToken or IFormFile) continue;
            if ((name.Equals("deviceId", StringComparison.OrdinalIgnoreCase)
                || name.Equals("machineId", StringComparison.OrdinalIgnoreCase)) && value is string id)
                yield return id;
            else foreach (var reference in DeviceReferenceReader.Read(JsonSerializer.SerializeToElement(value, JsonOptions)))
                yield return reference;
        }
    }
}
