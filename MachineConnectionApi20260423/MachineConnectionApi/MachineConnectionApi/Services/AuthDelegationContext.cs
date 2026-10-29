namespace MachineConnectionApi.Services;

public sealed record AuthDelegation(string? SessionToken, bool InternalService = false);

public static class AuthDelegationContext
{
    private static readonly AsyncLocal<AuthDelegation?> Ambient = new();
    public static AuthDelegation? Current => Ambient.Value;

    public static AuthDelegation? Capture(HttpContext? context)
    {
        if (Ambient.Value is { } delegated) return delegated;
        var token = context?.Request.Headers["X-Auth-Token"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(token) ? null : new AuthDelegation(token);
    }

    public static IDisposable Push(AuthDelegation? delegated)
    {
        var previous = Ambient.Value;
        Ambient.Value = delegated;
        return new Restore(previous);
    }

    private sealed class Restore(AuthDelegation? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
