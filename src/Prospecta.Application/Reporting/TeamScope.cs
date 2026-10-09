using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Common;
using Prospecta.Application.Security;

namespace Prospecta.Application.Reporting;

/// <summary>Whose figures the caller may build or read: themselves, their reporting line (managers), or everyone (Report.ViewAll).</summary>
public sealed class TeamScope(IAppDbContext db, ICurrentUser user)
{
    public bool IsAll => user.HasPermission(Permissions.ReportViewAll);

    /// <summary>Null means "no restriction" (organization-wide).</summary>
    public async Task<IReadOnlyList<Guid>?> UserIdsAsync(CancellationToken ct = default)
    {
        var me = user.Id ?? throw new ForbiddenException();
        if (IsAll) return null;
        var ids = new HashSet<Guid> { me };
        if (!user.HasPermission(Permissions.ReportViewTeam)) return ids.ToList();
        var frontier = new List<Guid> { me };
        for (var depth = 0; depth < 4 && frontier.Count > 0; depth++)
        {
            var next = await db.AppUsers.Where(u => u.ManagerId != null && frontier.Contains(u.ManagerId.Value)).Select(u => u.Id).ToListAsync(ct);
            frontier = next.Where(ids.Add).ToList();
        }

        return ids.ToList();
    }

    /// <summary>True when <paramref name="owner"/> sits below the caller in the reporting line.</summary>
    public async Task<bool> IsInTeamAsync(Guid owner, CancellationToken ct = default)
    {
        var ids = await UserIdsAsync(ct);
        return ids is null || ids.Contains(owner);
    }
}
