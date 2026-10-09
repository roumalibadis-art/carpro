using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Businesses;
using Prospecta.Application.Common;
using Prospecta.Application.Security;
using Prospecta.Domain.Common;
using Prospecta.Domain.Prospecting;

namespace Prospecta.Application.Prospecting;

public sealed class CampaignInput
{
    public string Name { get; set; } = string.Empty;
    public string? Objective { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? WilayaId { get; set; }
    public Guid? DairaId { get; set; }
    public Guid? CommuneId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public Guid? ManagerUserId { get; set; }
    public int? VisitTarget { get; set; }
    public decimal? Budget { get; set; }
    public string? Notes { get; set; }
    public List<Guid> ParticipantIds { get; set; } = [];
}

public sealed record CampaignListItem(Guid Id, string Name, CampaignStatus Status, DateOnly StartDate, DateOnly EndDate, string Manager, int Targets, int VisitsDone, int? VisitTarget);

public sealed record CampaignDetail(Guid Id, CampaignInput Data, CampaignStatus Status, string Manager, string? Category, string? Zone,
    IReadOnlyList<(Guid Id, string Name)> Participants, int Targets, int AssignedTargets, int TargetsReached, int VisitsPlanned, int VisitsDone,
    decimal? Budget, decimal ExpensesPlanned, decimal ExpensesActual, bool CanManage);

public sealed record TargetDto(Guid BusinessId, string Business, string? Commune, Guid? AssignedUserId, string? AssignedTo, int VisitsDone, DateTime? LastVisitAt, string Processing, string Outcome);

public sealed class CampaignService(IAppDbContext db, ICurrentUser user, BusinessService businesses, IAuditService audit, TimeProvider clock)
{
    private bool Manages => user.HasPermission(Permissions.CampaignManage);

    private void RequireManage()
    {
        if (!user.IsAuthenticated || !Manages) throw new ForbiddenException();
    }

    /// <summary>Managers see every campaign; others only those they lead or take part in.</summary>
    private IQueryable<Campaign> Visible()
    {
        if (!user.IsAuthenticated) throw new ForbiddenException();
        var uid = user.Id!.Value;
        return Manages ? db.Campaigns : db.Campaigns.Where(c => c.ManagerUserId == uid || c.Participants.Any(p => p.UserId == uid));
    }

    public async Task<PagedResult<CampaignListItem>> ListAsync(CampaignStatus? status, string? search, int page, int pageSize, CancellationToken ct = default)
    {
        (page, pageSize) = Paging.Clamp(page, pageSize);
        var q = Visible().AsNoTracking();
        if (status is not null) q = q.Where(c => c.Status == status);
        if (!string.IsNullOrWhiteSpace(search)) { var s = search.Trim(); q = q.Where(c => c.Name.Contains(s)); }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(c => c.StartDate).ThenBy(c => c.Id).Skip((page - 1) * pageSize).Take(pageSize).Select(c => new
        {
            c.Id, c.Name, c.Status, c.StartDate, c.EndDate, c.VisitTarget,
            Manager = db.AppUsers.Where(u => u.Id == c.ManagerUserId).Select(u => u.FullName).FirstOrDefault(),
            Targets = c.Targets.Count,
            Done = db.Visits.Count(v => v.CampaignId == c.Id && v.Status == VisitStatus.Done),
        }).ToListAsync(ct);
        return new PagedResult<CampaignListItem>(rows.Select(r => new CampaignListItem(r.Id, r.Name, r.Status, r.StartDate, r.EndDate, r.Manager ?? "?", r.Targets, r.Done, r.VisitTarget)).ToList(), total, page, pageSize);
    }

    public async Task<CampaignDetail> GetAsync(Guid id, CancellationToken ct = default)
    {
        var c = await Visible().AsNoTracking().Include(x => x.Participants).Include(x => x.Category).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        var userIds = c.Participants.Select(p => p.UserId).Append(c.ManagerUserId).ToList();
        var names = await db.AppUsers.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var zoneIds = new[] { c.CommuneId, c.DairaId, c.WilayaId }.Where(x => x is not null).Select(x => x!.Value).ToList();
        var zone = zoneIds.Count == 0 ? null : (await db.GeographicAreas.Where(a => a.Id == zoneIds[0]).Select(a => a.Name).FirstOrDefaultAsync(ct));
        var visits = db.Visits.Where(v => v.CampaignId == id);
        var reached = await visits.Where(v => v.Status == VisitStatus.Done).Select(v => v.BusinessId).Distinct().CountAsync(ct);
        var exp = (await db.Expenses.Where(e => e.CampaignId == id).Select(e => new { e.Kind, e.Amount }).ToListAsync(ct)) // summed client-side: portable decimal arithmetic
            .GroupBy(e => e.Kind).Select(g => new { g.Key, Sum = g.Sum(x => x.Amount) }).ToList();
        var input = new CampaignInput
        {
            Name = c.Name, Objective = c.Objective, CategoryId = c.CategoryId, WilayaId = c.WilayaId, DairaId = c.DairaId, CommuneId = c.CommuneId, StartDate = c.StartDate, EndDate = c.EndDate,
            ManagerUserId = c.ManagerUserId, VisitTarget = c.VisitTarget, Budget = c.Budget, Notes = c.Notes, ParticipantIds = c.Participants.Select(p => p.UserId).ToList(),
        };
        return new CampaignDetail(c.Id, input, c.Status, names.GetValueOrDefault(c.ManagerUserId, "?"), c.Category?.Name, zone,
            c.Participants.Select(p => (p.UserId, names.GetValueOrDefault(p.UserId, "?"))).ToList(),
            await db.CampaignTargets.CountAsync(t => t.CampaignId == id, ct), await db.CampaignTargets.CountAsync(t => t.CampaignId == id && t.AssignedUserId != null, ct), reached,
            await visits.CountAsync(v => v.WasPlanned, ct), await visits.CountAsync(v => v.Status == VisitStatus.Done, ct), c.Budget,
            exp.FirstOrDefault(e => e.Key == ExpenseKind.Planned)?.Sum ?? 0, exp.FirstOrDefault(e => e.Key == ExpenseKind.Actual)?.Sum ?? 0, Manages);
    }

    public async Task<Guid> SaveAsync(Guid? id, CampaignInput i, CancellationToken ct = default)
    {
        RequireManage();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(i.Name) || i.Name.Trim().Length > 200) errors.Add("Le nom de la campagne est obligatoire (200 caractères max).");
        if (i.Objective?.Length > 2000) errors.Add("Objectif trop long (2000 caractères max).");
        if (i.StartDate == default || i.EndDate == default) errors.Add("Les dates de début et de fin sont obligatoires.");
        else if (i.EndDate < i.StartDate) errors.Add("La date de fin doit être postérieure ou égale à la date de début.");
        if (i.VisitTarget is < 0) errors.Add("L'objectif de visites ne peut pas être négatif.");
        if (i.Budget is < 0) errors.Add("Le budget ne peut pas être négatif.");
        var managerId = i.ManagerUserId ?? user.Id!.Value;
        if (!await db.AppUsers.AnyAsync(u => u.Id == managerId && u.IsActive, ct)) errors.Add("Responsable introuvable ou désactivé.");
        var participants = i.ParticipantIds.Distinct().ToList();
        if (participants.Count > 0 && await db.AppUsers.CountAsync(u => participants.Contains(u.Id) && u.IsActive, ct) != participants.Count) errors.Add("Un participant est introuvable ou désactivé.");
        var geo = new BusinessInput { WilayaId = i.WilayaId, DairaId = i.DairaId, CommuneId = i.CommuneId, CategoryId = i.CategoryId };
        errors.AddRange(await businesses.ResolveGeographyAsync(geo, ct));
        if (i.CategoryId is { } cat && !await db.BusinessCategories.AnyAsync(c => c.Id == cat, ct)) errors.Add("Secteur inconnu.");
        if (errors.Count > 0) throw new ValidationException(errors);

        var now = clock.GetUtcNow().UtcDateTime;
        Campaign c;
        if (id is null)
        {
            c = new Campaign { CreatedAt = now, CreatedByUserId = user.Id };
            db.Campaigns.Add(c);
        }
        else
        {
            c = await db.Campaigns.Include(x => x.Participants).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
            if (c.Status is CampaignStatus.Completed or CampaignStatus.Cancelled) throw new ConflictException("Une campagne terminée ou annulée n'est plus modifiable.");
        }

        c.Name = i.Name.Trim(); c.Objective = i.Objective?.Trim(); c.CategoryId = i.CategoryId; c.WilayaId = geo.WilayaId; c.DairaId = geo.DairaId; c.CommuneId = geo.CommuneId;
        c.StartDate = i.StartDate; c.EndDate = i.EndDate; c.ManagerUserId = managerId; c.VisitTarget = i.VisitTarget; c.Budget = i.Budget; c.Notes = i.Notes?.Trim(); c.UpdatedAt = now;
        var wanted = participants.Append(managerId).Distinct().ToList();
        foreach (var p in c.Participants.Where(p => !wanted.Contains(p.UserId)).ToList()) db.CampaignParticipants.Remove(p);
        foreach (var uid in wanted.Where(w => c.Participants.All(p => p.UserId != w))) c.Participants.Add(new CampaignParticipant { CampaignId = c.Id, UserId = uid, CreatedAt = now });
        audit.Record(id is null ? "campaign.create" : "campaign.update", "Campaign", c.Id, c.Name);
        await db.SaveChangesAsync(ct);
        return c.Id;
    }

    public async Task SetStatusAsync(Guid id, CampaignStatus status, CancellationToken ct = default)
    {
        RequireManage();
        var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        var allowed = (c.Status, status) switch
        {
            (CampaignStatus.Draft, CampaignStatus.Active) or (CampaignStatus.Draft, CampaignStatus.Cancelled) => true,
            (CampaignStatus.Active, CampaignStatus.Completed) or (CampaignStatus.Active, CampaignStatus.Cancelled) => true,
            _ => false,
        };
        if (!allowed) throw new ConflictException($"Transition impossible : {c.Status} → {status}.");
        c.Status = status; c.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        audit.Record("campaign.status", "Campaign", c.Id, status.ToString());
        await db.SaveChangesAsync(ct);
    }

    // ---------- Targets ----------
    public async Task<PagedResult<TargetDto>> TargetsAsync(Guid campaignId, int page, int pageSize, CancellationToken ct = default)
    {
        if (!await Visible().AnyAsync(c => c.Id == campaignId, ct)) throw new NotFoundException();
        (page, pageSize) = Paging.Clamp(page, pageSize);
        var uid = user.Id!.Value;
        var q = db.CampaignTargets.AsNoTracking().Where(t => t.CampaignId == campaignId);
        if (!Manages) q = q.Where(t => t.AssignedUserId == uid); // a participant works on the businesses assigned to them
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(t => t.Business!.NormalizedName).ThenBy(t => t.Id).Skip((page - 1) * pageSize).Take(pageSize).Select(t => new
        {
            t.BusinessId, Name = t.Business!.Name, Commune = t.Business.Commune!.Name, t.AssignedUserId,
            To = db.AppUsers.Where(u => u.Id == t.AssignedUserId).Select(u => u.FullName).FirstOrDefault(),
            Done = db.Visits.Count(v => v.BusinessId == t.BusinessId && v.CampaignId == campaignId && v.Status == VisitStatus.Done),
            Last = db.Visits.Where(v => v.BusinessId == t.BusinessId && v.CampaignId == campaignId && v.Status == VisitStatus.Done).Max(v => (DateTime?)v.CompletedAt),
            Proc = t.Business.ProcessingStatus!.Label, Out = t.Business.OutcomeStatus!.Label,
        }).ToListAsync(ct);
        return new PagedResult<TargetDto>(rows.Select(r => new TargetDto(r.BusinessId, r.Name, r.Commune, r.AssignedUserId, r.To, r.Done, r.Last, r.Proc, r.Out)).ToList(), total, page, pageSize);
    }

    public Task<int> AddTargetsAsync(Guid campaignId, IReadOnlyCollection<Guid> businessIds, Guid? assigneeId, CancellationToken ct = default) =>
        AddCoreAsync(campaignId, businessIds, assigneeId, ct);

    /// <summary>Targets every business matching the caller's current filter (max 500), within the caller's own visibility.</summary>
    public async Task<int> AddTargetsByFilterAsync(Guid campaignId, BusinessFilter filter, Guid? assigneeId, CancellationToken ct = default)
    {
        RequireManage();
        var ids = await businesses.Filtered(filter).OrderBy(b => b.Id).Select(b => b.Id).Take(501).ToListAsync(ct);
        if (ids.Count > 500) throw new ValidationException("Plus de 500 entreprises correspondent : affinez les filtres.");
        return await AddCoreAsync(campaignId, ids, assigneeId, ct);
    }

    private async Task<int> AddCoreAsync(Guid campaignId, IReadOnlyCollection<Guid> businessIds, Guid? assigneeId, CancellationToken ct)
    {
        RequireManage();
        if (businessIds.Count is 0 or > 500) throw new ValidationException("Sélectionnez entre 1 et 500 entreprises.");
        var c = await db.Campaigns.Include(x => x.Participants).FirstOrDefaultAsync(x => x.Id == campaignId, ct) ?? throw new NotFoundException();
        if (c.Status is CampaignStatus.Completed or CampaignStatus.Cancelled) throw new ConflictException("Cette campagne est clôturée.");
        if (assigneeId is { } a && !await db.AppUsers.AnyAsync(u => u.Id == a && u.IsActive, ct)) throw new ValidationException("Utilisateur introuvable ou désactivé.");
        var now = clock.GetUtcNow().UtcDateTime;

        await using var tx = await UnitOfWork.BeginAsync(db.Database, ct);
        var allowed = await businesses.Scoped().Where(b => businessIds.Contains(b.Id)).Select(b => b.Id).ToListAsync(ct);
        var existing = (await db.CampaignTargets.Where(t => t.CampaignId == campaignId && businessIds.Contains(t.BusinessId)).ToListAsync(ct)).ToDictionary(t => t.BusinessId);
        var changed = 0;
        foreach (var bid in allowed)
        {
            if (existing.TryGetValue(bid, out var t))
            {
                if (assigneeId is not null && t.AssignedUserId != assigneeId) { t.AssignedUserId = assigneeId; changed++; }
                continue;
            }

            db.CampaignTargets.Add(new CampaignTarget { CampaignId = campaignId, BusinessId = bid, AssignedUserId = assigneeId, CreatedAt = now });
            changed++;
        }

        if (assigneeId is { } uid)
        {
            if (c.Participants.All(p => p.UserId != uid)) c.Participants.Add(new CampaignParticipant { CampaignId = c.Id, UserId = uid, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            await businesses.AssignAsync(allowed, uid, ct); // the salesperson gets access to these businesses (and their processing status moves to "assigned")
        }

        audit.Record("campaign.targets", "Campaign", campaignId, $"added/updated={changed}; assignee={assigneeId}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return changed;
    }

    public async Task RemoveTargetAsync(Guid campaignId, Guid businessId, CancellationToken ct = default)
    {
        RequireManage();
        var t = await db.CampaignTargets.FirstOrDefaultAsync(x => x.CampaignId == campaignId && x.BusinessId == businessId, ct) ?? throw new NotFoundException();
        if (await db.Visits.AnyAsync(v => v.CampaignId == campaignId && v.BusinessId == businessId, ct))
            throw new ConflictException("Des actions sont déjà enregistrées pour cette entreprise dans la campagne : elle ne peut plus être retirée.");
        db.CampaignTargets.Remove(t);
        audit.Record("campaign.target.remove", "Campaign", campaignId, businessId.ToString());
        await db.SaveChangesAsync(ct);
    }
}
