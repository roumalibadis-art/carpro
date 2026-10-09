using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Businesses;
using Prospecta.Application.Common;
using Prospecta.Application.Security;
using Prospecta.Domain.Common;
using Prospecta.Domain.Prospecting;

namespace Prospecta.Application.Prospecting;

public sealed class FollowUpInput
{
    public Guid BusinessId { get; set; }
    public Guid? AssignedUserId { get; set; }
    public DateOnly DueDate { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Priority Priority { get; set; } = Priority.Normal;
    public Guid? CampaignId { get; set; }
}

public sealed class FollowUpFilter
{
    public Guid? UserId { get; set; }
    public FollowUpStatus? Status { get; set; }
    /// <summary>"overdue" | "upcoming" (next 7 days) | null.</summary>
    public string? When { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? BusinessId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public sealed record FollowUpDto(Guid Id, Guid BusinessId, string Business, Guid AssignedUserId, string AssignedTo, DateOnly DueDate, string Reason, Priority Priority,
    FollowUpStatus Status, string? Result, DateOnly? NextDueDate, bool Overdue, DateTime? CompletedAt);

public sealed class FollowUpService(IAppDbContext db, ICurrentUser user, BusinessService businesses, IAuditService audit, TimeProvider clock)
{
    private bool SeesAll => user.HasPermission(Permissions.ActivityViewAll);

    private void RequireRecord()
    {
        if (!user.IsAuthenticated || !user.HasPermission(Permissions.ActivityRecord)) throw new ForbiddenException();
    }

    /// <summary>Own follow-ups only, unless the caller is allowed to see team activity.</summary>
    private IQueryable<FollowUp> Visible()
    {
        RequireRecord();
        var uid = user.Id!.Value;
        return SeesAll ? db.FollowUps : db.FollowUps.Where(f => f.AssignedUserId == uid);
    }

    public FollowUp Build(Guid businessId, Guid assignee, DateOnly due, string reason, Priority priority, Guid? visitId, Guid? campaignId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var f = new FollowUp
        {
            BusinessId = businessId, AssignedUserId = assignee, DueDate = due, Reason = reason.Trim(), Priority = priority, VisitId = visitId, CampaignId = campaignId,
            CreatedByUserId = user.Id, CreatedAt = now, UpdatedAt = now,
        };
        db.FollowUps.Add(f);
        return f;
    }

    public async Task<FollowUpDto> CreateAsync(FollowUpInput i, CancellationToken ct = default)
    {
        RequireRecord();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(i.Reason)) errors.Add("Le motif de la relance est obligatoire.");
        else if (i.Reason.Length > 500) errors.Add("Motif trop long (500 caractères max).");
        if (i.DueDate.Year < 2000) errors.Add("Date d'échéance invalide.");
        if (errors.Count > 0) throw new ValidationException(errors);

        var assignee = i.AssignedUserId ?? user.Id!.Value;
        if (assignee != user.Id && !SeesAll) throw new ForbiddenException("Vous ne pouvez créer des relances que pour vous-même.");
        if (!await db.AppUsers.AnyAsync(u => u.Id == assignee && u.IsActive, ct)) throw new ValidationException("Utilisateur introuvable ou désactivé.");
        if (!await businesses.Scoped().AnyAsync(b => b.Id == i.BusinessId, ct)) throw new NotFoundException();

        var f = Build(i.BusinessId, assignee, i.DueDate, i.Reason, i.Priority, null, i.CampaignId);
        audit.Record("followup.create", "FollowUp", f.Id, $"business={i.BusinessId}; due={i.DueDate:yyyy-MM-dd}");
        await db.SaveChangesAsync(ct);
        return await GetAsync(f.Id, ct);
    }

    public async Task<FollowUpDto> GetAsync(Guid id, CancellationToken ct = default) =>
        (await ProjectAsync(Visible().Where(f => f.Id == id), ct)).FirstOrDefault() ?? throw new NotFoundException();

    public async Task<PagedResult<FollowUpDto>> SearchAsync(FollowUpFilter f, CancellationToken ct = default)
    {
        var (page, size) = Paging.Clamp(f.Page, f.PageSize);
        var today = Dates.Today(clock);
        var q = Visible();
        if (f.UserId is not null) q = q.Where(x => x.AssignedUserId == f.UserId);
        if (f.CampaignId is not null) q = q.Where(x => x.CampaignId == f.CampaignId);
        if (f.BusinessId is not null) q = q.Where(x => x.BusinessId == f.BusinessId);
        if (f.Status is not null) q = q.Where(x => x.Status == f.Status);
        if (f.When == "overdue") q = q.Where(x => x.Status == FollowUpStatus.ToDo && x.DueDate < today);
        else if (f.When == "upcoming") { var end = today.AddDays(7); q = q.Where(x => x.Status == FollowUpStatus.ToDo && x.DueDate >= today && x.DueDate <= end); }
        // Only follow-ups on businesses the caller may still see (a reassigned business hides its follow-ups from the previous owner).
        var visibleBusinesses = businesses.Scoped().Select(b => b.Id);
        q = q.Where(x => visibleBusinesses.Contains(x.BusinessId));
        var total = await q.CountAsync(ct);
        var rows = await ProjectAsync(q.OrderBy(x => x.DueDate).ThenByDescending(x => x.Priority).ThenBy(x => x.Id).Skip((page - 1) * size).Take(size), ct);
        return new PagedResult<FollowUpDto>(rows, total, page, size);
    }

    public async Task<int> CountOverdueAsync(CancellationToken ct = default)
    {
        var today = Dates.Today(clock);
        return await Visible().CountAsync(x => x.Status == FollowUpStatus.ToDo && x.DueDate < today, ct);
    }

    private async Task<List<FollowUpDto>> ProjectAsync(IQueryable<FollowUp> q, CancellationToken ct)
    {
        var today = Dates.Today(clock);
        var rows = await q.AsNoTracking().Select(f => new
        {
            f.Id, f.BusinessId, Business = f.Business!.Name, f.AssignedUserId,
            To = db.AppUsers.Where(u => u.Id == f.AssignedUserId).Select(u => u.FullName).FirstOrDefault(),
            f.DueDate, f.Reason, f.Priority, f.Status, f.Result, f.NextDueDate, f.CompletedAt,
        }).ToListAsync(ct);
        return rows.Select(r => new FollowUpDto(r.Id, r.BusinessId, r.Business, r.AssignedUserId, r.To ?? "?", r.DueDate, r.Reason, r.Priority, r.Status, r.Result,
            r.NextDueDate, r.Status == FollowUpStatus.ToDo && r.DueDate < today, r.CompletedAt)).ToList();
    }

    private async Task<FollowUp> OpenAsync(Guid id, CancellationToken ct)
    {
        var f = await Visible().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        if (f.Status is FollowUpStatus.Done or FollowUpStatus.Cancelled) throw new ConflictException("Cette relance est déjà clôturée.");
        return f;
    }

    /// <summary>Marks the follow-up done; an optional next due date creates the next follow-up (the chain is kept, nothing is overwritten).</summary>
    public async Task CompleteAsync(Guid id, string? result, DateOnly? nextDue, CancellationToken ct = default)
    {
        var f = await OpenAsync(id, ct);
        if (result?.Length > 2000) throw new ValidationException("Résultat trop long (2000 caractères max).");
        var now = clock.GetUtcNow().UtcDateTime;
        await using var tx = await UnitOfWork.BeginAsync(db.Database, ct);
        f.Status = FollowUpStatus.Done; f.Result = result?.Trim(); f.NextDueDate = nextDue; f.CompletedAt = now; f.UpdatedAt = now;
        if (nextDue is { } n) Build(f.BusinessId, f.AssignedUserId, n, "Relance suivante : " + f.Reason, f.Priority, f.VisitId, f.CampaignId);
        audit.Record("followup.complete", "FollowUp", f.Id, nextDue is null ? null : $"next={nextDue:yyyy-MM-dd}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task PostponeAsync(Guid id, DateOnly newDue, CancellationToken ct = default)
    {
        var f = await OpenAsync(id, ct);
        if (newDue <= f.DueDate) throw new ValidationException("La nouvelle échéance doit être postérieure à l'échéance actuelle.");
        var old = f.DueDate;
        f.DueDate = newDue; f.Status = FollowUpStatus.ToDo; f.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        audit.Record("followup.postpone", "FollowUp", f.Id, $"{old:yyyy-MM-dd} -> {newDue:yyyy-MM-dd}");
        await db.SaveChangesAsync(ct);
    }

    public async Task CancelAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        var f = await OpenAsync(id, ct);
        f.Status = FollowUpStatus.Cancelled; f.Result = reason?.Trim(); f.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        audit.Record("followup.cancel", "FollowUp", f.Id, reason);
        await db.SaveChangesAsync(ct);
    }
}
