using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Common;
using Prospecta.Application.Identity;
using Prospecta.Web.Security;

namespace Prospecta.Web.Controllers;

public sealed record LoginRequest(string Email, string Password);

[ApiController, Route("api/v1/auth")]
public sealed class AuthController(LoginService login, JwtTokenService tokens, UserManager<ApplicationUser> users, ICurrentUser current) : ControllerBase
{
    [AllowAnonymous, HttpPost("login"), EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var (outcome, user) = await login.CheckAsync(request.Email, request.Password, ct);
        if (user is null) return Unauthorized(new { success = false, message = LoginService.Message(outcome), errors = Array.Empty<string>() });
        var (token, expires) = tokens.Create(user, await users.GetRolesAsync(user));
        return Ok(new { token, expiresAt = expires, user = new { user.Id, user.Email, user.FullName } });
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var user = await users.FindByIdAsync(current.Id!.Value.ToString()) ?? throw new NotFoundException();
        var perms = Prospecta.Application.Security.Permissions.All.Where(current.HasPermission).ToList();
        return Ok(new { user.Id, user.Email, user.FullName, roles = await users.GetRolesAsync(user), permissions = perms });
    }
}
