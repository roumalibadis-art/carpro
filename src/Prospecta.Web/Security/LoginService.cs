using Microsoft.AspNetCore.Identity;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Identity;
using Prospecta.Domain.Auditing;

namespace Prospecta.Web.Security;

public enum LoginOutcome { Success, Invalid, LockedOut, Disabled }

/// <summary>Credential check shared by the API and the web form. Failures are generic (no account enumeration).</summary>
public sealed class LoginService(UserManager<ApplicationUser> users, IAppDbContext db, IHttpContextAccessor http, TimeProvider clock)
{
    public async Task<(LoginOutcome Outcome, ApplicationUser? User)> CheckAsync(string email, string password, CancellationToken ct = default)
    {
        var user = string.IsNullOrWhiteSpace(email) ? null : await users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            await LogAsync(null, email, "login.failed", "unknown account", ct);
            return (LoginOutcome.Invalid, null);
        }

        if (await users.IsLockedOutAsync(user))
        {
            await LogAsync(user, email, "login.locked", null, ct);
            return (LoginOutcome.LockedOut, null);
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            await LogAsync(user, email, "login.failed", "bad password", ct);
            return (LoginOutcome.Invalid, null);
        }

        if (!user.IsActive)
        {
            await LogAsync(user, email, "login.disabled", null, ct);
            return (LoginOutcome.Disabled, null);
        }

        await users.ResetAccessFailedCountAsync(user);
        user.LastLoginAt = clock.GetUtcNow().UtcDateTime;
        await users.UpdateAsync(user);
        await LogAsync(user, email, "login.success", null, ct);
        return (LoginOutcome.Success, user);
    }

    public static string Message(LoginOutcome o) => o switch
    {
        LoginOutcome.LockedOut => "Compte temporairement verrouillé après trop de tentatives. Réessayez dans quelques minutes.",
        LoginOutcome.Disabled => "Ce compte est désactivé. Contactez un administrateur.",
        _ => "Identifiants incorrects.",
    };

    private async Task LogAsync(ApplicationUser? user, string email, string action, string? details, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            UserId = user?.Id, UserName = user?.Email ?? (email.Length > 200 ? email[..200] : email), Action = action, EntityType = "Auth",
            EntityId = user?.Id.ToString(), Details = details, IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString(), CreatedAt = clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
    }
}
