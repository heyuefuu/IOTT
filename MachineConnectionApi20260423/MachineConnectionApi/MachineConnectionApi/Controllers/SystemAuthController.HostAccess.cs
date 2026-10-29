namespace MachineConnectionApi.Controllers;

using IndustrialIoT.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

public sealed partial class SystemAuthController
{
    [HttpGet("host-access")]
    public IActionResult HostAccess([FromServices] IConfiguration configuration)
    {
        if (!ServiceTrustKey.Matches(ServiceTrustKey.Load(configuration["IndustrialIoT:ServiceKey"]),
                Request.Headers[ServiceTrustKey.Header].FirstOrDefault())) return Unauthorized();
        var session = _auth.Validate(Request.Headers["X-Auth-Token"].FirstOrDefault());
        return session is null ? Unauthorized() : Ok(new HostAccessGrant(session.UserId, session.Role,
            session.Permissions, session.DataScope, session.ExpiresAt));
    }
}
