using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Security;
using Prospecta.Infrastructure.Persistence;

namespace Prospecta.Web.Security;

/// <summary>
/// Runs for every authenticated request (cookie or JWT): re-checks the account is still active and rebuilds the permission
/// claims from the roles' current grants, so deactivation and permission changes apply immediately, not at token expiry.
/// </summary>
public sealed class PermissionClaimsTransformation(AppDbContext db, IPermissionStore store) : IClaimsTransformation
{
    private const string Marker = "prospecta:perms";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(c => c.Type == Marker)) return principal;
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return new ClaimsPrincipal(new ClaimsIdentity());

        var user = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => new { u.IsActive, u.SecurityStamp, u.FullName }).FirstOrDefaultAsync();
        var stamp = principal.FindFirstValue("stamp");
        if (user is null || !user.IsActive || (stamp is not null && stamp != user.SecurityStamp)) return new ClaimsPrincipal(new ClaimsIdentity());

        var roleNames = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        if (roleNames.Count == 0)
        {
            roleNames = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == id select r.Name!).ToListAsync();
        }

        var identity = new ClaimsIdentity();
        identity.AddClaim(new Claim(Marker, "1"));
        foreach (var r in roleNames.Where(r => !principal.HasClaim(ClaimTypes.Role, r))) identity.AddClaim(new Claim(ClaimTypes.Role, r));
        foreach (var p in await store.GetForRolesAsync(roleNames)) identity.AddClaim(new Claim(Permissions.ClaimType, p));
        principal.AddIdentity(identity);
        return principal;
    }
}

public static class ActiveUserCheck
{
    /// <summary>True when the account still exists, is active and the token/cookie security stamp is current.</summary>
    public static async Task<bool> IsValidAsync(ClaimsPrincipal principal, AppDbContext db)
    {
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub"), out var id)) return false;
        var user = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => new { u.IsActive, u.SecurityStamp }).FirstOrDefaultAsync();
        var stamp = principal.FindFirstValue("stamp");
        return user is { IsActive: true } && (stamp is null || stamp == user.SecurityStamp);
    }
}
