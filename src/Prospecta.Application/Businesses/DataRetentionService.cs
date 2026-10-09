using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Common;
using Prospecta.Application.Security;

namespace Prospecta.Application.Businesses;

public sealed record DeletedBusinessDto(Guid Id, string Name, DateTime DeletedAt, bool Merged, int Visits, int FollowUps);

/// <summary>
/// Erasure / retention: a business first goes to the "deleted" state (reversible by support, history kept). An administrator can then purge it
/// definitively — with every dependent record — when a correction or deletion request requires it. Each purge leaves an audit entry without the erased data.
/// </summary>
public sealed class DataRetentionService(IAppDbContext db, ICurrentUser user, IAuditService audit, TimeProvider clock)
{
    private void Require()
    {
        if (!user.IsAuthenticated || !user.HasPermission(Permissions.DataPurge)) throw new ForbiddenException();
    }

    public async Task<PagedResult<DeletedBusinessDto>> ListDeletedAsync(int page, int pageSize, CancellationToken ct = default)
    {
        Require();
        (page, pageSize) = Paging.Clamp(page, pageSize);
        var q = db.Businesses.IgnoreQueryFilters().AsNoTracking().Where(b => b.IsDeleted);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(b => b.DeletedAt).ThenBy(b => b.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(b => new { b.Id, b.Name, b.DeletedAt, b.MergedIntoId, Visits = db.Visits.Count(v => v.BusinessId == b.Id), Fu = db.FollowUps.Count(f => f.BusinessId == b.Id) }).ToListAsync(ct);
        return new PagedResult<DeletedBusinessDto>(rows.Select(r => new DeletedBusinessDto(r.Id, r.Name, r.DeletedAt ?? DateTime.MinValue, r.MergedIntoId is not null, r.Visits, r.Fu)).ToList(), total, page, pageSize);
    }

    public async Task PurgeAsync(Guid businessId, CancellationToken ct = default)
    {
        Require();
        await using var tx = await UnitOfWork.BeginAsync(db.Database, ct);
        var name = await PurgeCoreAsync(businessId, ct);
        audit.Record("data.purge", "Business", businessId, $"purged (name length {name.Length}); data erased");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Purges deleted businesses older than <paramref name="olderThanDays"/> (at least 30), at most 200 per run.</summary>
    public async Task<int> PurgeOlderThanAsync(int olderThanDays, CancellationToken ct = default)
    {
        Require();
        if (olderThanDays < 30) throw new ValidationException("Le délai minimal de conservation est de 30 jours.");
        var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-olderThanDays);
        var ids = await db.Businesses.IgnoreQueryFilters().Where(b => b.IsDeleted && b.DeletedAt != null && b.DeletedAt < cutoff).OrderBy(b => b.DeletedAt).Select(b => b.Id).Take(200).ToListAsync(ct);
        await using var tx = await UnitOfWork.BeginAsync(db.Database, ct);
        foreach (var id in ids) await PurgeCoreAsync(id, ct);
        audit.Record("data.purge.batch", "Business", null, $"count={ids.Count}; olderThanDays={olderThanDays}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ids.Count;
    }

    private async Task<string> PurgeCoreAsync(Guid id, CancellationToken ct)
    {
        var b = await db.Businesses.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        if (!b.IsDeleted) throw new ConflictException("Seule une entreprise déjà supprimée peut être purgée définitivement.");
        db.Visits.RemoveRange(await db.Visits.Where(v => v.BusinessId == id).ToListAsync(ct));
        db.FollowUps.RemoveRange(await db.FollowUps.Where(f => f.BusinessId == id).ToListAsync(ct));
        db.CampaignTargets.RemoveRange(await db.CampaignTargets.Where(t => t.BusinessId == id).ToListAsync(ct));
        db.BusinessHistory.RemoveRange(await db.BusinessHistory.Where(h => h.BusinessId == id).ToListAsync(ct));
        db.DuplicateCandidates.RemoveRange(await db.DuplicateCandidates.Where(c => c.BusinessAId == id || c.BusinessBId == id).ToListAsync(ct));
        foreach (var r in await db.CollectionResults.Where(r => r.BusinessId == id || r.MatchBusinessId == id).ToListAsync(ct)) { if (r.BusinessId == id) r.BusinessId = null; if (r.MatchBusinessId == id) r.MatchBusinessId = null; }
        foreach (var r in await db.ImportRows.Where(r => r.ImportedBusinessId == id || r.PotentialDuplicateOfId == id).ToListAsync(ct)) { if (r.ImportedBusinessId == id) r.ImportedBusinessId = null; if (r.PotentialDuplicateOfId == id) r.PotentialDuplicateOfId = null; }
        // Businesses merged into this one keep working: they only lose the pointer.
        foreach (var m in await db.Businesses.IgnoreQueryFilters().Where(x => x.MergedIntoId == id).ToListAsync(ct)) m.MergedIntoId = null;
        db.Businesses.Remove(b); // sources, provenance and assignments cascade
        return b.Name;
    }
}
