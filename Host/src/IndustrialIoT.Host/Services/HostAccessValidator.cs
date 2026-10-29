namespace IndustrialIoT.Host.Services;

using System.Net.Http.Json;
using IndustrialIoT.Security;

public sealed class HostAccessValidator(IHttpClientFactory clients, IConfiguration configuration)
{
    public const string GrantKey = "IndustrialIoT.HostAccess";
    public const string TrustedKey = "IndustrialIoT.TrustedService";

    public bool Trusted(string? supplied) => ServiceTrustKey.Matches(
        ServiceTrustKey.Load(configuration["IndustrialIoT:ServiceKey"]), supplied);

    public async Task<HostAccessGrant?> ValidateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/system/auth/host-access");
        request.Headers.Add(ServiceTrustKey.Header, ServiceTrustKey.Load(configuration["IndustrialIoT:ServiceKey"]));
        request.Headers.Add("X-Auth-Token", token);
        try
        {
            using var response = await clients.CreateClient("BusinessAuthorization").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var grant = await response.Content.ReadFromJsonAsync<HostAccessGrant>(cancellationToken: ct);
            return grant is not null && grant.ExpiresAt > DateTimeOffset.UtcNow ? grant : null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { return null; }
    }

    public async Task AuthenticateAsync(HttpContext context, RequestDelegate next)
    {
        if (!(context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs")))
        { await next(context); return; }
        if (Trusted(context.Request.Headers[ServiceTrustKey.Header].FirstOrDefault()))
        { context.Items[TrustedKey] = true; await next(context); return; }
        var token = context.Request.Headers["X-Auth-Token"].FirstOrDefault();
        if (context.Request.Path.StartsWithSegments("/hubs"))
        {
            token ??= context.Request.Query["access_token"].FirstOrDefault();
            var bearer = context.Request.Headers.Authorization.FirstOrDefault();
            if (token is null && bearer?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true) token = bearer[7..];
        }
        var grant = await ValidateAsync(token, context.RequestAborted);
        if (grant is null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "登录已过期或业务鉴权服务不可用。" });
            return;
        }
        context.Items[GrantKey] = grant;
        context.Items["IndustrialIoT.Token"] = token;
        await next(context);
    }
}
