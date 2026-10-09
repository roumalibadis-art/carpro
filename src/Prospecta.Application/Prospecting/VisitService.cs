using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Businesses;
using Prospecta.Application.Common;
using Prospecta.Application.Security;
using Prospecta.Domain.Businesses;
using Prospecta.Domain.Common;
using Prospecta.Domain.Geography;
using Prospecta.Domain.Prospecting;

namespace Prospecta.Application.Prospecting;

public sealed class VisitInput
{
    public Guid BusinessId { get; set; }
    public Guid? UserId { get; set; }
    public DateTime ScheduledAt { get; set; }
    public VisitAction Action { get; set; } = VisitAction.Visit;
    public Guid? CampaignId { get; set; }
    public Guid? OutingId { get; set; }
    public string? Comment { get; set; }
}

public sealed class VisitResult
{
    public string? ContactMet { get; set; }
    public InterestLevel? Interest { get; set; }
    public Guid? OutcomeStatusId { get; set; }
    public string? Objections { get; set; }
    public string? NeedIdentified { get; set; }
    public string? RequestedInfo { get; set; }
    public string? NextAction { get; set; }
    public DateOnly? NextFollowUpDate { get; set; }
    public string? Comment { get; set; }
}

public sealed class VisitFilter
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public VisitStatus? Status { get; set; }
    public Guid? UserId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? OutingId { get; set; }
    public Guid? BusinessId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public sealed record VisitDto(Guid Id, Guid BusinessId, string Business, Guid UserId, string User, Guid? CampaignId, Guid? OutingId, VisitAction Action, VisitStatus Status,
    bool WasPlanned, DateTime ScheduledAt, DateTime? CompletedAt, string? ContactMet, InterestLevel? Interest, string? OutcomeLabel, string? Objections, string? NeedIdentified,
    string? RequestedInfo, string? NextAction, DateOnly? NextFollowUpDate, string? Comment, string? CancelReason, Guid? PostponedFromId);

public sealed class VisitService(IAppDbContext db, ICurrentUser user, BusinessService businesses, FollowUpService followUps, IAuditService audit, TimeProvider clock)
{
    private bool SeesAll => user.HasPermission(Permissions.ActivityViewAll);

    private void RequireRecord()
    {
        if (!user.IsAuthenticated || !user.HasPermission(Permissions.ActivityRecord)) throw new ForbiddenException();
    }

    private static readonly Dictionary<string, int> Rank = new()
    {
        [StatusCodes.Unassigned] = 0, [StatusCodes.Assigned] = 1, ["to_contact"] = 2, ["contacted"] = 3, ["visit_planned"] = 3, ["to_follow_up"] = 3, ["visited"] = 4, ["done"] = 5,
    };

    private IQueryable<Visit> Visible()
    {
        RequireRecord();
        var uid = user.Id!.Value;
        return SeesAll ? db.Visits : db.Visits.Where(v => v.UserId == uid);
    }

    // ---------- Create ----------
    private async Task<(Visit Visit, Business Business)> BuildAsync(VisitInput i, bool planned, CancellationToken ct)
    {
        RequireRecord();
        var assignee = i.UserId ?? user.Id!.Value;
        if (assignee != user.Id && !SeesAll) throw new ForbiddenException("Vous ne pouvez planifier que vos propres actions.");
        if (!await db.AppUsers.AnyAsync(u => u.Id == assignee && u.IsActive, ct)) throw new ValidationException("Utilisateur introuvable ou désactivé.");
        if (i.Comment?.Length > 4000) throw new ValidationException("Commentaire trop long (4000 caractères max).");
        var business = await businesses.Scoped().Include(b => b.ProcessingStatus).Include(b => b.OutcomeStatus).FirstOrDefaultAsync(b => b.Id == i.BusinessId, ct) ?? throw new NotFoundException();
        if (i.CampaignId is { } c && !await db.Campaigns.AnyAsync(x => x.Id == c, ct)) throw new ValidationException("Campagne inconnue.");
        Guid? campaignId = i.CampaignId;
        if (i.OutingId is { } o)
        {
            var outing = await db.Outings.AsNoTracking().Where(x => x.Id == o).Select(x => new { x.CampaignId }).FirstOrDefaultAsync(ct) ?? throw new ValidationException("Sortie inconnue.");
            campaignId ??= outing.CampaignId; // an outing belongs to a campaign: its visits count towards it
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var v = new Visit
        {
            BusinessId = business.Id, UserId = assignee, WasPlanned = planned, CampaignId = campaignId, OutingId = i.OutingId, Action = i.Action, ScheduledAt = DateTime.SpecifyKind(i.ScheduledAt, DateTimeKind.Utc),
            Comment = i.Comment?.Trim(), CreatedAt = now, UpdatedAt = now,
        };
        db.Visits.Add(v);
        return (v, business);
    }

    public async Task<VisitDto> PlanAsync(VisitInput i, CancellationToken ct = default)
    {
        var (v, b) = await BuildAsync(i, true, ct);
        if (i.Action is VisitAction.Visit or VisitAction.Appointment or VisitAction.Demo) await AdvanceProcessingAsync(b, "visit_planned", ct);
        audit.Record("visit.plan", "Visit", v.Id, $"business={b.Id}; at={v.ScheduledAt:O}");
        await db.SaveChangesAsync(ct);
        return await GetAsync(v.Id, ct);
    }

    /// <summary>Records an interaction that already happened (e.g. an unplanned call) in one step.</summary>
    public async Task<VisitDto> LogDoneAsync(VisitInput i, VisitResult r, CancellationToken ct = default)
    {
        await using var tx = await UnitOfWork.BeginAsync(db.Database, ct);
        var (v, b) = await BuildAsync(i, false, ct);
        await ApplyResultAsync(v, b, r, ct);
        audit.Record("visit.log", "Visit", v.Id, $"business={b.Id}; action={v.Action}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(v.Id, ct);
    }

    public async Task<VisitDto> CompleteAsync(Guid id, VisitResult r, CancellationToken ct = default)
    {
        await using var tx = await UnitOfWork.BeginAsync(db.Database, ct);
        var v = await OpenAsync(id, ct);
        var b = await businesses.Scoped().Include(x => x.ProcessingStatus).Include(x => x.OutcomeStatus).FirstOrDefaultAsync(x => x.Id == v.BusinessId, ct) ?? throw new NotFoundException();
        await ApplyResultAsync(v, b, r, ct);
        audit.Record("visit.complete", "Visit", v.Id, $"business={b.Id}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(v.Id, ct);
    }

    private async Task ApplyResultAsync(Visit v, Business b, VisitResult r, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var (label, value, max) in new[] { ("Responsable rencontré", r.ContactMet, 200), ("Objections", r.Objections, 2000), ("Besoin identifié", r.NeedIdentified, 2000), ("Informations demandées", r.RequestedInfo, 2000), ("Prochaine action", r.NextAction, 500), ("Commentaire", r.Comment, 4000) })
            if (value?.Length > max) errors.Add($"{label} : {max} caractères maximum.");
        if (r.NextFollowUpDate is { } d && d < Dates.Today(clock).AddDays(-1)) errors.Add("La date de relance ne peut pas être dans le passé.");
        if (errors.Count > 0) throw new ValidationException(errors);

        StatusValue? outcome = null;
        if (r.OutcomeStatusId is { } oid)
            outcome = await db.StatusValues.FirstOrDefaultAsync(s => s.Id == oid && s.Kind == StatusKind.Outcome && s.IsActive, ct) ?? throw new ValidationException("Résultat commercial inconnu.");

        var now = clock.GetUtcNow().UtcDateTime;
        v.Status = VisitStatus.Done; v.CompletedAt = now; v.UpdatedAt = now;
        v.ContactMet = r.ContactMet?.Trim(); v.Interest = r.Interest; v.OutcomeStatusId = outcome?.Id; v.Objections = r.Objections?.Trim();
        v.NeedIdentified = r.NeedIdentified?.Trim(); v.RequestedInfo = r.RequestedInfo?.Trim(); v.NextAction = r.NextAction?.Trim(); v.NextFollowUpDate = r.NextFollowUpDate;
        if (!string.IsNullOrWhiteSpace(r.Comment)) v.Comment = string.IsNullOrWhiteSpace(v.Comment) ? r.Comment.Trim() : v.Comment + "\n" + r.Comment.Trim();

        // Explicit consequences of a recorded interaction; census status is never touched.
        await AdvanceProcessingAsync(b, v.Action is VisitAction.Call or VisitAction.FollowUp ? "contacted" : "visited", ct);
        if (outcome is not null && b.OutcomeStatusId != outcome.Id) businesses.ApplyStatus(b, StatusKind.Outcome, outcome);
        if (r.NextFollowUpDate is { } due)
            followUps.Build(b.Id, v.UserId, due, string.IsNullOrWhiteSpace(r.NextAction) ? "Relance suite à une interaction" : r.NextAction!, Priority.Normal, v.Id, v.CampaignId);
    }

    /// <summary>Moves the processing dimension forward only (never back), e.g. contacted → visited.</summary>
    private async Task AdvanceProcessingAsync(Business b, string code, CancellationToken ct)
    {
        var current = b.ProcessingStatus?.Code ?? StatusCodes.Unassigned;
        if (Rank.GetValueOrDefault(code, -1) <= Rank.GetValueOrDefault(current, 99) && current is not ("visit_planned" or "to_follow_up")) return;
        var target = await db.StatusValues.FirstOrDefaultAsync(s => s.Kind == StatusKind.Processing && s.Code == code && s.IsActive, ct);
        if (target is null || target.Id == b.ProcessingStatusId) return; // an administrator may have disabled this optional status
        businesses.ApplyStatus(b, StatusKind.Processing, target);
    }

    // ---------- Cancel / postpone ----------
    private async Task<Visit> OpenAsync(Guid id, CancellationToken ct)
    {
        var v = await Visible().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        if (v.Status != VisitStatus.Planned) throw new ConflictException($"Cette action est déjà « {Label(v.Status)} » : l'historique n'est pas modifiable.");
        return v;
    }

    private static string Label(VisitStatus s) => s switch { VisitStatus.Done => "réalisée", VisitStatus.Cancelled => "annulée", VisitStatus.Postponed => "reportée", _ => "planifiée" };

    public async Task CancelAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        var v = await OpenAsync(id, ct);
        v.Status = VisitStatus.Cancelled; v.CancelReason = reason?.Trim(); v.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        audit.Record("visit.cancel", "Visit", v.Id, reason);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The original stays as "postponed" (history); a new planned visit carries the new date.</summary>
    public async Task<VisitDto> PostponeAsync(Guid id, DateTime newScheduledAtUtc, CancellationToken ct = default)
    {
        await using var tx = await UnitOfWork.BeginAsync(db.Database, ct);
        var v = await OpenAsync(id, ct);
        if (newScheduledAtUtc <= v.ScheduledAt) throw new ValidationException("La nouvelle date doit être postérieure à la date prévue.");
        var now = clock.GetUtcNow().UtcDateTime;
        v.Status = VisitStatus.Postponed; v.UpdatedAt = now;
        var next = new Visit
        {
            BusinessId = v.BusinessId, UserId = v.UserId, CampaignId = v.CampaignId, OutingId = v.OutingId, Action = v.Action, Comment = v.Comment, WasPlanned = true,
            ScheduledAt = DateTime.SpecifyKind(newScheduledAtUtc, DateTimeKind.Utc), PostponedFromId = v.Id, CreatedAt = now, UpdatedAt = now,
        };
        db.Visits.Add(next);
        audit.Record("visit.postpone", "Visit", v.Id, $"-> {next.ScheduledAt:O}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(next.Id, ct);
    }

    // ---------- Read ----------
    public async Task<VisitDto> GetAsync(Guid id, CancellationToken ct = default) =>
        (await ProjectAsync(Visible().Where(v => v.Id == id), ct)).FirstOrDefault() ?? throw new NotFoundException();

    public async Task<PagedResult<VisitDto>> SearchAsync(VisitFilter f, CancellationToken ct = default)
    {
        var (page, size) = Paging.Clamp(f.Page, f.PageSize);
        var q = Visible();
        if (f.From is not null) q = q.Where(v => v.ScheduledAt >= f.From);
        if (f.To is not null) q = q.Where(v => v.ScheduledAt < f.To);
        if (f.Status is not null) q = q.Where(v => v.Status == f.Status);
        if (f.UserId is not null) q = q.Where(v => v.UserId == f.UserId);
        if (f.CampaignId is not null) q = q.Where(v => v.CampaignId == f.CampaignId);
        if (f.OutingId is not null) q = q.Where(v => v.OutingId == f.OutingId);
        if (f.BusinessId is not null) q = q.Where(v => v.BusinessId == f.BusinessId);
        var total = await q.CountAsync(ct);
        var rows = await ProjectAsync(q.OrderByDescending(v => v.ScheduledAt).ThenBy(v => v.Id).Skip((page - 1) * size).Take(size), ct);
        return new PagedResult<VisitDto>(rows, total, page, size);
    }

    /// <summary>Full interaction history of a business the caller can see (all users: shared data, nothing replaced by the latest).</summary>
    public async Task<IReadOnlyList<VisitDto>> HistoryForBusinessAsync(Guid businessId, CancellationToken ct = default)
    {
        RequireRecord();
        if (!await businesses.Scoped().AnyAsync(b => b.Id == businessId, ct)) throw new NotFoundException();
        return await ProjectAsync(db.Visits.Where(v => v.BusinessId == businessId).OrderByDescending(v => v.ScheduledAt).Take(200), ct);
    }

    private async Task<List<VisitDto>> ProjectAsync(IQueryable<Visit> q, CancellationToken ct)
    {
        var rows = await q.AsNoTracking().Select(v => new
        {
            v.Id, v.BusinessId, Business = v.Business!.Name, v.UserId, User = db.AppUsers.Where(u => u.Id == v.UserId).Select(u => u.FullName).FirstOrDefault(),
            v.CampaignId, v.OutingId, v.Action, v.Status, v.WasPlanned, v.ScheduledAt, v.CompletedAt, v.ContactMet, v.Interest,
            Outcome = db.StatusValues.Where(s => s.Id == v.OutcomeStatusId).Select(s => s.Label).FirstOrDefault(),
            v.Objections, v.NeedIdentified, v.RequestedInfo, v.NextAction, v.NextFollowUpDate, v.Comment, v.CancelReason, v.PostponedFromId,
        }).ToListAsync(ct);
        return rows.Select(r => new VisitDto(r.Id, r.BusinessId, r.Business, r.UserId, r.User ?? "?", r.CampaignId, r.OutingId, r.Action, r.Status, r.WasPlanned, r.ScheduledAt, r.CompletedAt,
            r.ContactMet, r.Interest, r.Outcome, r.Objections, r.NeedIdentified, r.RequestedInfo, r.NextAction, r.NextFollowUpDate, r.Comment, r.CancelReason, r.PostponedFromId)).ToList();
    }
}
