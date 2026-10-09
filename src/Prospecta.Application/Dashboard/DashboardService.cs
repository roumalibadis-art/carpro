using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Businesses;
using Prospecta.Application.Common;
using Prospecta.Domain.Auditing;
using Prospecta.Domain.Common;
using Prospecta.Domain.Geography;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Security;
using System.Text.Json;

namespace Prospecta.Application.Dashboard;

public sealed record CountItem(string Label, int Count);
public sealed record DashboardDto(
    int Total, int Verified, int ToVerify, int Unassigned, int WithoutPhone, int WithoutCoordinates, int PotentialDuplicates,
    IReadOnlyList<CountItem> ByCensus, IReadOnlyList<CountItem> ByProcessing, IReadOnlyList<CountItem> ByOutcome,
    IReadOnlyList<CountItem> ByCommune, IReadOnlyList<CountItem> ByCategory, IReadOnlyList<CountItem> BySource);

/// <summary>Phase-1 dashboard: census figures only. Visits, follow-ups, spending and conversions arrive with phase 2 and are not simulated.</summary>
public sealed class DashboardService(BusinessService businesses, IAppDbContext db)
{
    public async Task<DashboardDto> GetAsync(BusinessFilter filter, CancellationToken ct = default)
    {
        var q = businesses.Filtered(filter);
        var total = await q.CountAsync(ct);

        async Task<List<CountItem>> Group<TKey>(IQueryable<IGrouping<TKey, Domain.Businesses.Business>> g, Func<TKey, string> label) =>
            (await g.Select(x => new { x.Key, N = x.Count() }).OrderByDescending(x => x.N).Take(10).ToListAsync(ct)).Select(x => new CountItem(label(x.Key), x.N)).ToList();

        var census = (await q.GroupBy(b => b.CensusStatus!.Label).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).Select(x => new CountItem(x.Key, x.N)).OrderByDescending(x => x.Count).ToList();
        var proc = (await q.GroupBy(b => b.ProcessingStatus!.Label).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).Select(x => new CountItem(x.Key, x.N)).OrderByDescending(x => x.Count).ToList();
        var outcome = (await q.GroupBy(b => b.OutcomeStatus!.Label).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).Select(x => new CountItem(x.Key, x.N)).OrderByDescending(x => x.Count).ToList();

        var bySource = (await q.SelectMany(b => b.Sources).GroupBy(src => src.SourceType).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
            .Select(x => new CountItem(x.Key.ToString(), x.N)).OrderByDescending(x => x.Count).ToList();

        return new DashboardDto(
            total,
            await q.CountAsync(b => b.CensusStatus!.Code == StatusCodes.Verified, ct),
            await q.CountAsync(b => b.CensusStatus!.Code == StatusCodes.ToVerify || b.CensusStatus.Code == StatusCodes.Partial || b.CensusStatus.Code == StatusCodes.PotentialDuplicate, ct),
            await q.CountAsync(b => b.ProcessingStatus!.Code == StatusCodes.Unassigned, ct),
            await q.CountAsync(b => b.NormalizedPhone == null, ct),
            await q.CountAsync(b => b.Latitude == null || b.Longitude == null, ct),
            await q.CountAsync(b => db.DuplicateCandidates.Any(c => c.Status == DuplicateStatus.Pending && (c.BusinessAId == b.Id || c.BusinessBId == b.Id)), ct),
            census, proc, outcome,
            await Group(q.Where(b => b.Commune != null).GroupBy(b => b.Commune!.Name), k => k),
            await Group(q.Where(b => b.Category != null).GroupBy(b => b.Category!.Name), k => k),
            bySource);
    }
}

public sealed record AuditDto(DateTime At, string? User, string Action, string EntityType, string? EntityId, string? Details, string? Ip);

public sealed class AuditQueryService(IAppDbContext db, ICurrentUser user)
{
    public async Task<PagedResult<AuditDto>> ListAsync(string? action, string? userName, DateTime? from, DateTime? to, int page, int pageSize, CancellationToken ct = default)
    {
        if (!user.HasPermission(Permissions.AuditView)) throw new ForbiddenException();
        (page, pageSize) = Paging.Clamp(page, pageSize);
        var q = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action.StartsWith(action));
        if (!string.IsNullOrWhiteSpace(userName)) q = q.Where(a => a.UserName != null && a.UserName.Contains(userName));
        if (from is not null) q = q.Where(a => a.CreatedAt >= from);
        if (to is not null) q = q.Where(a => a.CreatedAt < to.Value.Date.AddDays(1));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new AuditDto(a.CreatedAt, a.UserName, a.Action, a.EntityType, a.EntityId, a.Details, a.IpAddress)).ToListAsync(ct);
        return new PagedResult<AuditDto>(rows, total, page, pageSize);
    }
}

public sealed record SavedFilterDto(Guid Id, string Name, SavedFilterScope Scope, bool Mine, BusinessFilter Filter);

public sealed class SavedFilterService(IAppDbContext db, ICurrentUser user, IAuditService audit, TimeProvider clock)
{
    public async Task<IReadOnlyList<SavedFilterDto>> ListAsync(CancellationToken ct = default)
    {
        var uid = user.Id ?? throw new ForbiddenException();
        var rows = await db.SavedFilters.AsNoTracking().Where(f => f.UserId == uid || f.Scope == SavedFilterScope.Shared).OrderBy(f => f.Name).Take(200).ToListAsync(ct);
        return rows.Select(f => new SavedFilterDto(f.Id, f.Name, f.Scope, f.UserId == uid, JsonSerializer.Deserialize<BusinessFilter>(f.FilterJson) ?? new())).ToList();
    }

    public async Task<Guid> SaveAsync(string name, BusinessFilter filter, SavedFilterScope scope, CancellationToken ct = default)
    {
        var uid = user.Id ?? throw new ForbiddenException();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new ValidationException("Le nom de la vue est obligatoire (80 caractères max).");
        if (scope == SavedFilterScope.Shared && !user.HasPermission(Permissions.BusinessViewAll)) throw new ForbiddenException("Le partage d'une vue est réservé aux responsables.");
        if (await db.SavedFilters.CountAsync(f => f.UserId == uid, ct) >= 100) throw new ValidationException("Limite de 100 vues atteinte.");
        filter.Page = 1;
        var entity = new SavedFilter { UserId = uid, Name = name.Trim(), Scope = scope, FilterJson = JsonSerializer.Serialize(filter), CreatedAt = clock.GetUtcNow().UtcDateTime };
        db.SavedFilters.Add(entity);
        audit.Record("filter.save", "SavedFilter", entity.Id, entity.Name);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var uid = user.Id ?? throw new ForbiddenException();
        var f = await db.SavedFilters.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct) ?? throw new NotFoundException();
        db.SavedFilters.Remove(f);
        audit.Record("filter.delete", "SavedFilter", id);
        await db.SaveChangesAsync(ct);
    }
}
