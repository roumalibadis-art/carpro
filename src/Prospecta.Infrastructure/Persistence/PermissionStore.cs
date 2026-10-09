using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Security;

namespace Prospecta.Infrastructure.Persistence;

/// <summary>Role → permission lookup from Identity role claims, cached briefly and invalidated on change.</summary>
public sealed class PermissionStore(AppDbContext db, IMemoryCache cache) : IPermissionStore
{
    private const string Key = "role-permissions";

    public async Task<IReadOnlySet<string>> GetForRolesAsync(IEnumerable<string> roles, CancellationToken ct = default)
    {
        var map = await cache.GetOrCreateAsync(Key, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            var rows = await (from c in db.RoleClaims.AsNoTracking()
                              join r in db.Roles.AsNoTracking() on c.RoleId equals r.Id
                              where c.ClaimType == Permissions.ClaimType
                              select new { r.Name, c.ClaimValue }).ToListAsync(ct);
            return rows.GroupBy(x => x.Name!).ToDictionary(g => g.Key, g => g.Select(x => x.ClaimValue!).ToHashSet());
        }) ?? new Dictionary<string, HashSet<string>>();

        return roles.Where(map.ContainsKey).SelectMany(r => map[r]).ToHashSet();
    }

    public void Invalidate() => cache.Remove(Key);
}
