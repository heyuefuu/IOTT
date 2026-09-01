namespace MachineConnectionApi.Services;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using MachineConnectionApi.Models;

/// <summary>用户库（App_Data/system-users.json）的唯一入口，权限管理与登录鉴权共用。</summary>
public interface IUserStore
{
    List<AppUserDto> ReadAll();
    void WriteAll(IEnumerable<AppUserDto> items);
    TResult Update<TResult>(Func<List<AppUserDto>, TResult> update);
}

public sealed class UserStore : IUserStore
{
    private readonly JsonFileStore<AppUserDto> _store = new("system-users.json");

    public List<AppUserDto> ReadAll() => _store.ReadAll();

    public void WriteAll(IEnumerable<AppUserDto> items) => _store.WriteAll(items);

    public TResult Update<TResult>(Func<List<AppUserDto>, TResult> update) => _store.Update(update);
}

/// <summary>PBKDF2-SHA256 口令散列，存储格式 iterations.saltBase64.hashBase64。</summary>
public static class PasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return false;
        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record AuthSession(string Token, string UserId, string Username, string Name, string Role, IReadOnlyList<string> Permissions, DateTimeOffset ExpiresAt)
{
    public bool HasPermission(string key) =>
        Role == "admin" || Permissions.Contains("all") || Permissions.Contains(key);
}

public interface IAuthService
{
    /// <summary>校验用户名口令；成功返回会话，失败返回 null 并给出原因。</summary>
    (AuthSession? Session, string? Error) Login(string username, string password);
    void Logout(string token);
    AuthSession? Validate(string? token);
    /// <summary>吊销指定用户的全部在线会话。用户被删除/禁用/权限或密码变更后调用，避免旧 token 继续生效。</summary>
    void RevokeUserSessions(string userId);
    /// <summary>确保存在可登录账号：若无任何带凭据的用户，创建/升级默认管理员 admin（初始口令 admin@123）。</summary>
    void EnsureSeeded();
}

public sealed class AuthService : IAuthService
{
    public const string DefaultAdminUsername = "admin";
    public const string DefaultAdminPassword = "admin@123";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, AuthSession> _sessions = new();
    private readonly IUserStore _users;
    private readonly ISystemActivityLog _activityLog;
    private readonly ILogger<AuthService> _logger;
    private readonly object _seedGate = new();

    public AuthService(IUserStore users, ISystemActivityLog activityLog, ILogger<AuthService> logger)
    {
        _users = users;
        _activityLog = activityLog;
        _logger = logger;
    }

    public void EnsureSeeded()
    {
        lock (_seedGate)
        {
            var seeded = _users.Update(rows =>
            {
                if (rows.Any(x => !string.IsNullOrWhiteSpace(x.PasswordHash)))
                    return false;
                var hash = PasswordHasher.Hash(DefaultAdminPassword);
                var index = rows.FindIndex(x => x.Username.Equals(
                    DefaultAdminUsername, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    rows[index] = rows[index] with
                    {
                        PasswordHash = hash, Role = "admin",
                        Status = "启用", Permissions = ["all"],
                    };
                }
                else
                {
                    rows.Add(new AppUserDto
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Username = DefaultAdminUsername, Name = "系统管理员",
                        Password = "******", Role = "admin", Status = "启用",
                        Permissions = ["all"], PasswordHash = hash,
                    });
                }
                return true;
            });
            if (!seeded) return;
            _logger.LogWarning("已创建/激活默认管理员 {Username}（初始密码 {Password}），请登录后尽快修改", DefaultAdminUsername, DefaultAdminPassword);
            _activityLog.Write("warning", "初始化默认管理员",
                $"账号 {DefaultAdminUsername}（初始密码 {DefaultAdminPassword}），请尽快在权限管理中修改密码");
        }
    }

    public (AuthSession? Session, string? Error) Login(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return (null, "用户名和密码不能为空");

        EnsureSeeded();
        var user = _users.ReadAll().FirstOrDefault(x => x.Username.Equals(
            username.Trim(), StringComparison.OrdinalIgnoreCase));
        if (user is null) return (null, "用户名或密码错误");
        if (user.Status != "启用") return (null, "账号已被禁用");
        if (string.IsNullOrWhiteSpace(user.PasswordHash))
            return (null, "该账号尚未设置登录密码，请管理员在权限管理中重置密码");
        if (!PasswordHasher.Verify(password, user.PasswordHash))
            return (null, "用户名或密码错误");

        var login = _users.Update<(AppUserDto? User, string? Error)>(rows =>
        {
            var index = rows.FindIndex(x => x.Id == user.Id);
            if (index < 0) return (null, "用户已不存在");
            var current = rows[index];
            if (current.Status != "启用") return (null, "账号已被禁用");
            if (!PasswordHasher.Verify(password, current.PasswordHash))
                return (null, "登录凭据已变更，请重新输入密码");
            var updated = current with
            {
                LastLogin = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            rows[index] = updated;
            return (updated, null);
        });
        if (login.User is null) return (null, login.Error);
        user = login.User;

        var session = new AuthSession(
            Token: Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            UserId: user.Id,
            Username: user.Username,
            Name: string.IsNullOrWhiteSpace(user.Name) ? user.Username : user.Name,
            Role: user.Role,
            Permissions: user.Permissions,
            ExpiresAt: DateTimeOffset.Now.Add(SessionLifetime));
        _sessions[session.Token] = session;
        _activityLog.Write("operation", "用户登录", $"{user.Name}（{user.Username}）", user.Username);
        return (session, null);
    }

    public void Logout(string token)
    {
        if (_sessions.TryRemove(token, out var session))
            _activityLog.Write("operation", "用户退出", session.Username, session.Username);
    }

    public AuthSession? Validate(string? token)
    {
        SweepExpiredSessions();
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (!_sessions.TryGetValue(token, out var session)) return null;
        if (session.ExpiresAt < DateTimeOffset.Now)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }
        return session;
    }

    public void RevokeUserSessions(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        foreach (var (token, session) in _sessions)
        {
            if (session.UserId == userId)
                _sessions.TryRemove(token, out _);
        }
    }

    private DateTimeOffset _nextSweepAt = DateTimeOffset.MinValue;

    /// <summary>清理过期会话。用户关浏览器不登出的 token 否则会永久留在字典里；限频每 10 分钟一次。</summary>
    private void SweepExpiredSessions()
    {
        var now = DateTimeOffset.Now;
        if (now < _nextSweepAt) return;
        _nextSweepAt = now.AddMinutes(10);
        foreach (var (token, session) in _sessions)
        {
            if (session.ExpiresAt < now)
                _sessions.TryRemove(token, out _);
        }
    }
}
