namespace MachineConnectionApi.Controllers;

using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/system/auth")]
public sealed class SystemAuthController : ControllerBase
{
    private readonly IAuthService _auth;

    public SystemAuthController(IAuthService auth) => _auth = auth;

    /// <summary>用户名口令登录，返回会话 token（后续请求经 X-Auth-Token 头携带）。</summary>
    [HttpPost("login")]
    public ActionResult<LoginResponse> Login([FromBody] LoginRequest request)
    {
        var (session, error) = _auth.Login(request.Username, request.Password);
        if (session is null)
            return Unauthorized(new { error });
        return Ok(new LoginResponse(session.Token,
            new LoginUserInfo(session.UserId, session.Username, session.Name, session.Role, session.Permissions)));
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        var token = Request.Headers["X-Auth-Token"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(token))
            _auth.Logout(token);
        return NoContent();
    }

    /// <summary>校验当前 token 并返回会话用户信息（前端恢复登录态用）。</summary>
    [HttpGet("me")]
    public ActionResult<LoginUserInfo> Me()
    {
        var session = _auth.Validate(Request.Headers["X-Auth-Token"].FirstOrDefault());
        if (session is null)
            return Unauthorized(new { error = "登录已过期" });
        return Ok(new LoginUserInfo(session.UserId, session.Username, session.Name, session.Role, session.Permissions));
    }
}
