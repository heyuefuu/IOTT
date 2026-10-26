namespace MachineConnectionApi.Tests;

using MachineConnectionApi.Controllers;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// 账号系统防误删回归：任何 CRUD 都必须保证至少保留一个"可用管理员"
/// （启用 + 有口令 + 具备管理员权限，且权限判定与运行时 AuthSession.HasPermission 一致）。
/// </summary>
internal static class AccountSafetyRegressionTests
{
    public static void RunAll()
    {
        // 1) 唯一管理员：删除/降权/清权限/停用均被拒绝
        var store = new MemoryUserStore();
        var controller = Build(store);
        store.WriteAll([Admin("a1", "admin")]);

        ExpectConflict(controller.Delete("a1").GetType(), "删除唯一管理员应被拒绝");
        ExpectConflict(controller.Update("a1", Admin("a1", "admin") with { Role = "viewer", Permissions = [] }).Result?.GetType(), "降权唯一管理员应被拒绝");
        ExpectConflict(controller.Update("a1", Admin("a1", "admin") with { Status = "停用" }).Result?.GetType(), "停用唯一管理员应被拒绝");
        AssertEqual(1, store.ReadAll().Count, "唯一管理员应仍在");
        AssertEqual(true, IsAdmin(store.ReadAll()[0]), "唯一管理员权限未被改动");
        // 角色型管理员（Role=admin）清空权限列表后仍是管理员 → 允许
        var clearPerms = controller.UpdatePermissions("a1", []);
        AssertEqual(true, clearPerms is OkObjectResult, "角色型管理员清空权限列表应允许（仍为 admin）");
        AssertEqual(true, IsAdmin(store.ReadAll()[0]), "清空权限列表后仍为管理员");

        // 1b) 权限型管理员（Role!=admin，靠权限 all）：清空权限应被拒绝
        var storeP = new MemoryUserStore();
        var controllerP = Build(storeP);
        storeP.WriteAll([Admin("p1", "viewer") with { Permissions = ["all"] }]);
        ExpectConflict(controllerP.UpdatePermissions("p1", []).GetType(), "清空唯一权限型管理员权限应被拒绝");
        AssertEqual(true, IsAdmin(storeP.ReadAll()[0]), "权限型管理员权限未被改动");

        // 2) 权限判定与运行时一致：Role="Admin"（大小写不同）不算管理员
        store.WriteAll([Admin("c1", "Admin") with { Permissions = [] }, Admin("a2", "admin")]);
        controller.Update("c1", Admin("c1", "Admin") with { Permissions = [] }); // 允许：其本就不是可用管理员
        AssertEqual(2, store.ReadAll().Count, "大小写非管理员的账号可正常更新");
        ExpectConflict(controller.Delete("a2").GetType(), "删除唯一真正管理员应被拒绝（含非管理员 caller）");

        // 3) 存在两个可用管理员时：可删除其一、可降权其一
        store.WriteAll([Admin("a1", "admin"), Admin("a3", "admin")]);
        var deleteResult = controller.Delete("a1");
        AssertEqual(true, deleteResult is NoContentResult, "两管理员时可删除其一");
        AssertEqual(1, store.ReadAll().Count, "删除后剩一个");
        AssertEqual(true, IsAdmin(store.ReadAll()[0]), "剩余为管理员");

        store.WriteAll([Admin("a1", "admin"), Admin("a4", "admin")]);
        var demote = controller.Update("a4", Admin("a4", "admin") with { Role = "viewer", Permissions = [] });
        AssertEqual(true, demote.Result is OkObjectResult, "两管理员时可降权其一");
        AssertEqual(true, IsAdmin(store.ReadAll().First(x => x.Id == "a1")), "另一管理员仍在");

        // 4) 带角色赋予的管理员同样受保护（Create 时 Roles -> 合并权限含 all）
        var roles = new MemoryRoleStore();
        roles.WriteAll([new RoleDto { Id = "r-adm", Key = "系统管理员", Name = "系统管理员", Permissions = ["all"], CreatedAt = DateTimeOffset.Now }]);
        var store2 = new MemoryUserStore();
        var controller2 = Build(store2, roles);
        var created = controller2.Create(Admin("b1", "viewer") with { Permissions = [], Roles = ["系统管理员"], Password = "p@ss" });
        AssertEqual(true, created.Result is OkObjectResult, "按角色创建管理员应成功");
        var createdValue = (AppUserDto)((OkObjectResult)created.Result!).Value!;
        AssertEqual(true, createdValue.Permissions.Contains("all"), "角色权限应合并进生效权限");
        ExpectConflict(controller2.Delete(createdValue.Id).GetType(), "经角色获得的管理员亦不可被删除");
    }

    private static SystemUsersController Build(IUserStore store, IRoleStore? roles = null)
    {
        var log = new NullLog();
        var auth = new AuthService(store, log, NullLogger<AuthService>.Instance);
        return new SystemUsersController(store, roles ?? new MemoryRoleStore(), log, auth);
    }

    private static AppUserDto Admin(string id, string role) => new()
    {
        Id = id, Username = id, Name = id, Role = role, Status = "启用",
        Permissions = ["all"], Password = "******", PasswordHash = "h",
    };

    private static bool IsAdmin(AppUserDto u) =>
        string.Equals(u.Role, "admin", StringComparison.Ordinal)
        || u.Permissions.Contains("all", StringComparer.Ordinal);

    private static void ExpectConflict(Type? actualType, string name)
    {
        if (actualType != typeof(ConflictObjectResult))
            throw new InvalidOperationException($"{name}: expected ConflictObjectResult, actual {actualType?.Name ?? "null"}");
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
    }

    private sealed class MemoryUserStore : IUserStore
    {
        private readonly object _gate = new();
        private List<AppUserDto> _rows = [];

        public List<AppUserDto> ReadAll() { lock (_gate) return _rows.ToList(); }
        public void WriteAll(IEnumerable<AppUserDto> items) { lock (_gate) _rows = items.ToList(); }
        public TResult Update<TResult>(Func<List<AppUserDto>, TResult> update)
        {
            lock (_gate) { var copy = _rows.ToList(); var result = update(copy); _rows = copy; return result; }
        }
    }

    private sealed class MemoryRoleStore : IRoleStore
    {
        private readonly object _gate = new();
        private List<RoleDto> _rows = [];
        public List<RoleDto> ReadAll() { lock (_gate) return _rows.ToList(); }
        public void WriteAll(IEnumerable<RoleDto> items) { lock (_gate) _rows = items.ToList(); }
        public TResult Update<TResult>(Func<List<RoleDto>, TResult> update)
        {
            lock (_gate) { var copy = _rows.ToList(); var result = update(copy); _rows = copy; return result; }
        }
    }

    private sealed class NullLog : ISystemActivityLog
    {
        public List<SystemLogDto> ReadAll() => [];
        public List<SystemLogDto> ReadArchive() => [];
        public SystemLogDto Append(SystemLogDto entry) => entry;
        public void Write(string type, string action, string detail, string user = "系统", string ip = "-", string device = "", string result = "") { }
    }
}
