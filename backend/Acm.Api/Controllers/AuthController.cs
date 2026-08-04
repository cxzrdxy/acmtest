using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Acm.Api.Data;
using Acm.Api.Dtos;
using Acm.Api.Services;

namespace Acm.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController(AuthService auth, TokenService tokens, IOptions<AppOptions> app)
    : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<TokenResponse>> Register(RegisterRequest req)
    {
        // 个人自用：可通过 App__AllowRegister=false 关闭自助注册
        if (!app.Value.AllowRegister)
            throw new ApiException(403, "管理员已关闭注册");
        var user = await auth.RegisterAsync(req);
        var resp = new TokenResponse(tokens.CreateAccessToken(user.Id),
                                     new UserRead(user.Id, user.Username));
        return StatusCode(201, resp);
    }

    [HttpPost("login")]
    public async Task<ActionResult<TokenResponse>> Login(LoginRequest req)
    {
        var user = await auth.LoginAsync(req);
        return new TokenResponse(tokens.CreateAccessToken(user.Id),
                                 new UserRead(user.Id, user.Username));
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserRead>> Me([FromServices] AppDbContext db)
    {
        var user = await db.Users.FindAsync(this.CurrentUserId());
        return new UserRead(user!.Id, user.Username);
    }

    [Authorize]
    [HttpPut("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest req)
    {
        await auth.ChangePasswordAsync(this.CurrentUserId(), req);
        return Ok(new { msg = "ok" });
    }
}
