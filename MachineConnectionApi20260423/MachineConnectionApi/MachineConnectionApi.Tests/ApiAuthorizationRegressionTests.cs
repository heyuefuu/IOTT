namespace MachineConnectionApi.Tests;

using System.Reflection;
using MachineConnectionApi.Controllers;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ApiAuthorizationRegressionTests
{
    public static void RunAll()
    {
        var users = new MemoryUserStore();
        var auth = new AuthService(users, new NullActivityLog(), NullLogger<AuthService>.Instance);
        var filter = new BusinessApiAuthorizationFilter(auth);
        foreach (var controller in typeof(VerifyTasksController).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type)))
        foreach (var action in controller.GetMethods().Where(method => method.IsDefined(typeof(HttpMethodAttribute))))
        {
            var expected = controller == typeof(SystemAuthController) && action.Name == "Login" ? 200 : 401;
            Expect(expected, Check(filter, controller, action.Name, null), $"Anonymous {controller.Name}.{action.Name}");
        }

        var reader = Login(auth, users, "reader", ["data_read"]);
        Expect(200, Check<DataReadWriteController>(filter, "ReadTags", reader), "Reader POST read");
        Expect(200, Check<ProgramTransferController>(filter, "Download", reader), "Reader POST download");
        Expect(403, Check<DataReadWriteController>(filter, "WriteTags", reader), "Reader write");
        Expect(403, Check<ProgramTransferController>(filter, "Upload", reader), "Reader upload");
        Expect(403, Check<DevicesController>(filter, "Delete", reader), "Reader device delete");
        Expect(403, Check<VerifyTasksController>(filter, "Run", reader), "Reader task run");
        Expect(403, Check<CsController>(filter, "StartServer", reader), "Reader server start");
        Expect(403, Check<SystemUsersController>(filter, "Create", reader), "Reader user create");
        Expect(200, Check<SystemUsersController>(filter, "ListPermissions", reader), "Permission dictionary");
        Expect(401, Check<VerifyTasksController>(filter, "Create", "invalid-token"), "Invalid session");
        var configurationManager = Login(auth, users, "configuration-manager", ["config_manage"]);
        var deviceManager = Login(auth, users, "device-manager", ["device_manage"]);
        foreach (var action in new[] { "Get", "Put", "Test" })
        {
            Expect(200, Check<InfluxSettingsController>(filter, action, configurationManager), "Configuration manager Influx settings");
            Expect(403, Check<InfluxSettingsController>(filter, action, reader), "Reader Influx settings");
            Expect(403, Check<InfluxSettingsController>(filter, action, deviceManager), "Device manager Influx settings");
        }
        auth.RevokeUserSessions("reader");
        Expect(401, Check<DataReadWriteController>(filter, "ReadTags", reader), "Revoked session");
        var reporter = Login(auth, users, "reporter", ["report_manage"]);
        Expect(200, Check<VerifyTasksController>(filter, "Run", reporter), "Reporter task run");
        Expect(200, Check<DevicesController>(filter, "List", reporter), "Reporter device selector");
        Expect(200, Check<TelemetryInfluxController>(filter, "QueryHistory", reporter), "Reporter history");
        Expect(200, Check<CsController>(filter, "ParallelTest", reporter), "Reporter parallel test");
        Expect(403, Check<DevicesController>(filter, "Create", reporter), "Reporter device mutation");
        var writer = Login(auth, users, "writer", ["data_write"]);
        Expect(200, Check<DataReadWriteController>(filter, "WriteTags", writer), "Writer write");
        Expect(403, Check<SystemUsersController>(filter, "List", writer), "Writer users");
        var administrator = Login(auth, users, "administrator", [], "admin");
        Expect(200, Check<VerifyTasksController>(filter, "Create", administrator), "Admin task create");
        var all = Login(auth, users, "all", ["all"]);
        Expect(200, Check<SystemUsersController>(filter, "Create", all), "All permission");
        foreach (var action in new[] { "Get", "Put", "Test" })
        {
            Expect(200, Check<InfluxSettingsController>(filter, action, administrator), "Admin Influx settings");
            Expect(200, Check<InfluxSettingsController>(filter, action, all), "All Influx settings");
        }
    }

    private static void Expect(int expected, int actual, string name)
    {
        if (expected != actual)
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
    }

    private sealed class MemoryUserStore : IUserStore
    {
        private List<AppUserDto> users = [];
        public List<AppUserDto> ReadAll() => users.ToList();
        public void WriteAll(IEnumerable<AppUserDto> items) => users = items.ToList();
        public TResult Update<TResult>(Func<List<AppUserDto>, TResult> update) => update(users);
    }

    private sealed class NullActivityLog : ISystemActivityLog
    {
        public List<SystemLogDto> ReadAll() => [];
        public SystemLogDto Append(SystemLogDto entry) => entry;
        public void Write(string type, string action, string detail, string user = "system", string ip = "-") { }
    }

    private static string Login(AuthService auth, MemoryUserStore users, string username,
        string[] permissions, string role = "user")
    {
        users.Update(rows =>
        {
            rows.Add(new AppUserDto
            {
                Id = username, Username = username, Name = username, Role = role,
                Status = "启用", Permissions = permissions, PasswordHash = PasswordHasher.Hash("fixture-password"),
            });
            return 0;
        });
        return auth.Login(username, "fixture-password").Session?.Token
            ?? throw new InvalidOperationException("Fixture login failed");
    }

    private static int Check<TController>(BusinessApiAuthorizationFilter filter, string action, string? token)
        => Check(filter, typeof(TController), action, token);

    private static int Check(BusinessApiAuthorizationFilter filter, Type controller, string action, string? token)
    {
        var http = new DefaultHttpContext();
        if (token is not null) http.Request.Headers["X-Auth-Token"] = token;
        var descriptor = new ControllerActionDescriptor
        {
            ControllerName = controller.Name[..^"Controller".Length], ActionName = action,
            EndpointMetadata = controller.GetMethod(action)!.GetCustomAttributes().Cast<object>().ToList(),
        };
        var context = new AuthorizationFilterContext(new ActionContext(http, new RouteData(), descriptor, new ModelStateDictionary()), []);
        filter.OnAuthorization(context);
        return (context.Result as ObjectResult)?.StatusCode ?? 200;
    }
}
