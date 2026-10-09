using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Common;
using Prospecta.Application.Security;
using Prospecta.Domain.Common;
using Prospecta.Domain.Prospecting;

namespace Prospecta.Application.Prospecting;

public sealed class OutingInput
{
    public DateOnly Date { get; set; }
    public TimeOnly? DepartureTime { get; set; }
    public int? DurationMinutes { get; set; }
    public string? StartPoint { get; set; }
    public string? Zone { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? ManagerUserId { get; set; }
    public string? Observations { get; set; }
    public List<Guid> ParticipantIds { get; set; } = [];
}

public sealed class ExpenseInput
{
    public Guid? OutingId { get; set; }
    public Guid? CampaignId { get; set; }
    public ExpenseKind Kind { get; set; } = ExpenseKind.Actual;
    public ExpenseCategory Category { get; set; } = ExpenseCategory.Transport;
    public decimal Amount { get; set; }
    public string? Description { get; set; }
    public DateOnly Date { get; set; }
    public string? ReceiptReference { get; set; }
}

public sealed record ExpenseDto(Guid Id, ExpenseKind Kind, ExpenseCategory Category, decimal Amount, string? Description, DateOnly Date, string? Receipt, string? By);

public sealed record OutingListItem(Guid Id, DateOnly Date, string? Zone, string? Campaign, OutingStatus Status, string Manager, int Planned, int Done);

public sealed record OutingDetail(Guid Id, OutingInput Data, OutingStatus Status, string Manager, string? Campaign, IReadOnlyList<(Guid Id, string Name)> Participants,
    int VisitsPlanned, int VisitsDone, int VisitsCancelled, int VisitsPostponed, decimal PlannedTotal, decimal ActualTotal,
    IReadOnlyDictionary<ExpenseCategory, decimal> ActualByCategory, IReadOnlyList<ExpenseDto> Expenses, bool CanManage);

public sealed class OutingService(IAppDbContext db, ICurrentUser user, IAuditService audit, TimeProvider clock)
{
    private bool Manages => user.HasPermission(Permissions.OutingManage);

    private void RequireManage()
    {
        if (!user.IsAuthenticated || !Manages) throw new ForbiddenException();
    }

    private IQueryable<Outing> Visible()
    {
        if (!user.IsAuthenticated) throw new ForbiddenException();
        var uid = user.Id!.Value;
        return Manages ? db.Outings : db.Outings.Where(o => o.ManagerUserId == uid || o.Participants.Any(p => p.UserId == uid));
    }

    public async Task<PagedResult<OutingListItem>> ListAsync(DateOnly? from, DateOnly? to, Guid? campaignId, int page, int pageSize, CancellationToken ct = default)
    {
        (page, pageSize) = Paging.Clamp(page, pageSize);
        var q = Visible().AsNoTracking();
        if (from is not null) q = q.Where(o => o.Date >= from);
        if (to is not null) q = q.Where(o => o.Date <= to);
        if (campaignId is not null) q = q.Where(o => o.CampaignId == campaignId);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(o => o.Date).ThenBy(o => o.Id).Skip((page - 1) * pageSize).Take(pageSize).Select(o => new
        {
            o.Id, o.Date, o.Zone, Campaign = o.Campaign!.Name, o.Status,
            Manager = db.AppUsers.Where(u => u.Id == o.ManagerUserId).Select(u => u.FullName).FirstOrDefault(),
            Planned = db.Visits.Count(v => v.OutingId == o.Id && v.WasPlanned), Done = db.Visits.Count(v => v.OutingId == o.Id && v.Status == VisitStatus.Done),
        }).ToListAsync(ct);
        return new PagedResult<OutingListItem>(rows.Select(r => new OutingListItem(r.Id, r.Date, r.Zone, r.Campaign, r.Status, r.Manager ?? "?", r.Planned, r.Done)).ToList(), total, page, pageSize);
    }

    public async Task<OutingDetail> GetAsync(Guid id, CancellationToken ct = default)
    {
        var o = await Visible().AsNoTracking().Include(x => x.Participants).Include(x => x.Campaign).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        var ids = o.Participants.Select(p => p.UserId).Append(o.ManagerUserId).ToList();
        var names = await db.AppUsers.Where(u => ids.Contains(u.Id)).ToDictionary2Async(ct);
        var counts = await db.Visits.Where(v => v.OutingId == id).GroupBy(v => v.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var planned = await db.Visits.CountAsync(v => v.OutingId == id && v.WasPlanned, ct);
        var expenses = await db.Expenses.AsNoTracking().Where(e => e.OutingId == id).OrderBy(e => e.Date).ThenBy(e => e.Id).ToListAsync(ct);
        var input = new OutingInput
        {
            Date = o.Date, DepartureTime = o.DepartureTime, DurationMinutes = o.DurationMinutes, StartPoint = o.StartPoint, Zone = o.Zone, CampaignId = o.CampaignId,
            ManagerUserId = o.ManagerUserId, Observations = o.Observations, ParticipantIds = o.Participants.Select(p => p.UserId).ToList(),
        };
        return new OutingDetail(o.Id, input, o.Status, names.GetValueOrDefault(o.ManagerUserId, "?"), o.Campaign?.Name,
            o.Participants.Select(p => (p.UserId, names.GetValueOrDefault(p.UserId, "?"))).ToList(), planned,
            counts.FirstOrDefault(c => c.Key == VisitStatus.Done)?.N ?? 0, counts.FirstOrDefault(c => c.Key == VisitStatus.Cancelled)?.N ?? 0, counts.FirstOrDefault(c => c.Key == VisitStatus.Postponed)?.N ?? 0,
            expenses.Where(e => e.Kind == ExpenseKind.Planned).Sum(e => e.Amount), expenses.Where(e => e.Kind == ExpenseKind.Actual).Sum(e => e.Amount),
            expenses.Where(e => e.Kind == ExpenseKind.Actual).GroupBy(e => e.Category).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount)),
            expenses.Select(e => new ExpenseDto(e.Id, e.Kind, e.Category, e.Amount, e.Description, e.Date, e.ReceiptReference, null)).ToList(), Manages);
    }

    public async Task<Guid> SaveAsync(Guid? id, OutingInput i, CancellationToken ct = default)
    {
        RequireManage();
        var errors = new List<string>();
        if (i.Date == default) errors.Add("La date de la sortie est obligatoire.");
        if (i.DurationMinutes is <= 0 or > 1440) errors.Add("La durée doit être comprise entre 1 et 1440 minutes.");
        if (i.StartPoint?.Length > 300 || i.Zone?.Length > 300) errors.Add("Point de départ / zone : 300 caractères maximum.");
        if (i.Observations?.Length > 4000) errors.Add("Observations trop longues (4000 caractères max).");
        if (i.CampaignId is { } c && !await db.Campaigns.AnyAsync(x => x.Id == c, ct)) errors.Add("Campagne inconnue.");
        var managerId = i.ManagerUserId ?? user.Id!.Value;
        var participants = i.ParticipantIds.Distinct().ToList();
        var wanted = participants.Append(managerId).Distinct().ToList();
        if (await db.AppUsers.CountAsync(u => wanted.Contains(u.Id) && u.IsActive, ct) != wanted.Count) errors.Add("Un responsable ou participant est introuvable ou désactivé.");
        if (errors.Count > 0) throw new ValidationException(errors);

        var now = clock.GetUtcNow().UtcDateTime;
        Outing o;
        if (id is null) { o = new Outing { CreatedAt = now }; db.Outings.Add(o); }
        else
        {
            o = await db.Outings.Include(x => x.Participants).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
            if (o.Status != OutingStatus.Planned) throw new ConflictException("Une sortie réalisée ou annulée n'est plus modifiable (ajoutez des observations via la clôture).");
        }

        o.Date = i.Date; o.DepartureTime = i.DepartureTime; o.DurationMinutes = i.DurationMinutes; o.StartPoint = i.StartPoint?.Trim(); o.Zone = i.Zone?.Trim();
        o.CampaignId = i.CampaignId; o.ManagerUserId = managerId; o.Observations = i.Observations?.Trim(); o.UpdatedAt = now;
        foreach (var p in o.Participants.Where(p => !wanted.Contains(p.UserId)).ToList()) db.OutingParticipants.Remove(p);
        foreach (var uid in wanted.Where(w => o.Participants.All(p => p.UserId != w))) o.Participants.Add(new OutingParticipant { OutingId = o.Id, UserId = uid, CreatedAt = now });
        audit.Record(id is null ? "outing.create" : "outing.update", "Outing", o.Id, $"{o.Date:yyyy-MM-dd} {o.Zone}");
        await db.SaveChangesAsync(ct);
        return o.Id;
    }

    /// <summary>Closes (or cancels) an outing; observations can still be added at that point.</summary>
    public async Task CloseAsync(Guid id, OutingStatus status, string? observations, CancellationToken ct = default)
    {
        RequireManage();
        if (status == OutingStatus.Planned) throw new ValidationException("Statut de clôture invalide.");
        var o = await db.Outings.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        if (o.Status != OutingStatus.Planned) throw new ConflictException("Cette sortie est déjà clôturée.");
        if (observations?.Length > 4000) throw new ValidationException("Observations trop longues (4000 caractères max).");
        o.Status = status; o.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        if (!string.IsNullOrWhiteSpace(observations)) o.Observations = string.IsNullOrWhiteSpace(o.Observations) ? observations.Trim() : o.Observations + "\n" + observations.Trim();
        audit.Record("outing.close", "Outing", o.Id, status.ToString());
        await db.SaveChangesAsync(ct);
    }

    // ---------- Expenses ----------
    public async Task<Guid> AddExpenseAsync(ExpenseInput i, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated || !user.HasPermission(Permissions.ActivityRecord)) throw new ForbiddenException();
        var errors = new List<string>();
        if (i.Amount <= 0 || i.Amount > 100_000_000m) errors.Add("Le montant doit être strictement positif.");
        if (decimal.Round(i.Amount, 2) != i.Amount) errors.Add("Le montant ne peut avoir plus de 2 décimales.");
        if (i.OutingId is null && i.CampaignId is null) errors.Add("Rattachez la dépense à une sortie ou à une campagne.");
        if (i.Date == default) errors.Add("La date de la dépense est obligatoire.");
        if (i.Description?.Length > 500 || i.ReceiptReference?.Length > 200) errors.Add("Description (500) ou référence de justificatif (200) trop longue.");
        if (errors.Count > 0) throw new ValidationException(errors);

        if (i.OutingId is { } oid)
        {
            // Planned amounts are a managers' budget; actual ones can be entered by whoever took part in the outing.
            var o = await Visible().FirstOrDefaultAsync(x => x.Id == oid, ct) ?? throw new NotFoundException();
            if (i.Kind == ExpenseKind.Planned && !Manages) throw new ForbiddenException("Seul un responsable peut saisir des dépenses prévisionnelles.");
            if (o.Status == OutingStatus.Cancelled) throw new ConflictException("Cette sortie est annulée.");
        }
        else if (!Manages) throw new ForbiddenException("Les dépenses de campagne sont saisies par un responsable.");
        if (i.CampaignId is { } cid && !await db.Campaigns.AnyAsync(c => c.Id == cid, ct)) throw new ValidationException("Campagne inconnue.");

        var e = new Expense
        {
            OutingId = i.OutingId, CampaignId = i.CampaignId ?? (i.OutingId is { } o2 ? await db.Outings.Where(x => x.Id == o2).Select(x => x.CampaignId).FirstOrDefaultAsync(ct) : null),
            Kind = i.Kind, Category = i.Category, Amount = i.Amount, Description = i.Description?.Trim(), Date = i.Date, UserId = user.Id,
            ReceiptReference = i.ReceiptReference?.Trim(), CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.Expenses.Add(e);
        audit.Record("expense.add", "Expense", e.Id, $"{e.Kind} {e.Category} {e.Amount}");
        await db.SaveChangesAsync(ct);
        return e.Id;
    }

    public async Task DeleteExpenseAsync(Guid id, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated) throw new ForbiddenException();
        var e = await db.Expenses.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        if (e.UserId != user.Id && !Manages) throw new NotFoundException();
        db.Expenses.Remove(e);
        audit.Record("expense.delete", "Expense", id, $"{e.Kind} {e.Amount}");
        await db.SaveChangesAsync(ct);
    }
}

internal static class QueryExt
{
    public static async Task<Dictionary<Guid, string>> ToDictionary2Async(this IQueryable<Prospecta.Application.Identity.ApplicationUser> q, CancellationToken ct) =>
        await q.ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
}
