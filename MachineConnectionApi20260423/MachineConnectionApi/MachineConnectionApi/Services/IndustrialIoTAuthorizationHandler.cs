namespace MachineConnectionApi.Services;

using IndustrialIoT.Security;

public sealed class IndustrialIoTAuthorizationHandler(IHttpContextAccessor accessor, IConfiguration configuration,
    IAuthService auth) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Remove(ServiceTrustKey.Header);
        request.Headers.Remove("X-Auth-Token");
        var delegated = AuthDelegationContext.Capture(accessor.HttpContext);
        if (delegated?.InternalService == true)
            request.Headers.Add(ServiceTrustKey.Header, ServiceTrustKey.Load(configuration["IndustrialIoT:ServiceKey"]));
        else if (!string.IsNullOrWhiteSpace(delegated?.SessionToken)
            && auth.Validate(delegated.SessionToken) is not null)
            request.Headers.Add("X-Auth-Token", delegated.SessionToken);
        else return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"Missing or revoked delegated identity\"}",
                System.Text.Encoding.UTF8, "application/json"),
        });
        return base.SendAsync(request, ct);
    }
}
