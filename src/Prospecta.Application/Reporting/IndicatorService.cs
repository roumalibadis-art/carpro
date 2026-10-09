using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Common;
using Prospecta.Application.Prospecting;
using Prospecta.Application.Security;
using Prospecta.Domain.Common;
using Prospecta.Domain.Prospecting;

namespace Prospecta.Application.Reporting;

public sealed class IndicatorFilter
{
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? CommuneId { get; set; }
    public Guid? CategoryId { get; set; }
}

public sealed record Indicator(string Key, string Label, string Definition, int? Numerator, int? Denominator, decimal? Value, string Unit, decimal? PreviousValue, decimal? Delta)
{
    public bool Computable => Value is not null;
    public string Display => Value is null ? "Non calculable" : Unit == "%" ? $"{Value:0.#} %" : $"{Value:N2} {Unit}";
}

public sealed record Counts(int Assigned, int Treated, int Contacted, int Calls, int VisitsPlanned, int VisitsDone, int FollowUpsDue, int FollowUpsDone, int FollowUpsOverdue,
    int Interested, int Appointments, int DemosRequested, int ProposalsSent, int Clients, decimal ExpensesActual, decimal ExpensesPlanned, int VisitsWithResult);

public sealed record IndicatorSet(DateOnly From, DateOnly To, DateOnly PreviousFrom, DateOnly PreviousTo, IReadOnlyList<Indicator> Indicators, Counts Counts, IReadOnlyList<string> Filters, double? AverageCompleteness);

/// <summary>
/// The six evaluation indicators with explicit numerators/denominators. Missing data is never a negative result:
/// a zero denominator yields "Non calculable" (null), and distinct businesses are counted separately from actions.
/// </summary>
public sealed class IndicatorService(IAppDbContext db, ICurrentUser user, TeamScope team, TimeProvider clock)
{
    private static readonly HashSet<string> InterestedOutcomes = ["interested", "appointment_requested", "demo_requested", "proposal_sent", "negotiation", "won"];
    private static readonly VisitAction[] FieldActions = [VisitAction.Visit, VisitAction.Appointment, VisitAction.Demo];

    private sealed record V(Guid BusinessId, Guid UserId, VisitAction Action, VisitStatus Status, bool WasPlanned, InterestLevel? Interest, string? Outcome, DateTime At);
    private sealed record Raw(List<V> Visits, List<(Guid Biz, Guid User)> Assigned, List<(DateOnly Due, FollowUpStatus Status)> FollowUps, decimal Actual, decimal Planned, double? Completeness);

    public async Task<IndicatorSet> ComputeAsync(IndicatorFilter f, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated || !user.HasPermission(Permissions.ReportCreate)) throw new ForbiddenException();
        if (f.To < f.From) throw new ValidationException("La période est invalide : la fin précède le début.");
        if (f.To.DayNumber - f.From.DayNumber > 731) throw new ValidationException("La période ne peut pas dépasser deux ans.");

        var scope = await team.UserIdsAsync(ct);
        IReadOnlyList<Guid>? users = scope;
        if (f.UserId is { } uid)
        {
            if (scope is not null && !scope.Contains(uid)) throw new NotFoundException(); // someone outside the caller's line behaves as non-existent
            users = [uid];
        }

        var length = f.To.DayNumber - f.From.DayNumber + 1;
        var pTo = f.From.AddDays(-1);
        var pFrom = pTo.AddDays(-(length - 1)); // comparable period: same length, immediately before
        var cur = await LoadAsync(f, f.From, f.To, users, ct);
        var prev = await LoadAsync(f, pFrom, pTo, users, ct);
        var today = Dates.Today(clock);
        var (c, ind) = Build(cur, today);
        var (_, prevInd) = Build(prev, today);
        var list = ind.Select(i => { var p = prevInd.First(x => x.Key == i.Key); return i with { PreviousValue = p.Value, Delta = i.Value is not null && p.Value is not null ? i.Value - p.Value : null }; }).ToList();

        var applied = new List<string> { $"Période : {f.From:dd/MM/yyyy} → {f.To:dd/MM/yyyy}" };
        if (f.CampaignId is { } cid) applied.Add("Campagne : " + (await db.Campaigns.Where(x => x.Id == cid).Select(x => x.Name).FirstOrDefaultAsync(ct) ?? "?"));
        if (f.UserId is { } u2) applied.Add("Utilisateur : " + (await db.AppUsers.Where(x => x.Id == u2).Select(x => x.FullName).FirstOrDefaultAsync(ct) ?? "?"));
        else applied.Add(scope is null ? "Utilisateurs : toute l'organisation" : scope.Count == 1 ? "Utilisateur : moi" : $"Utilisateurs : mon équipe ({scope.Count})");
        if (f.CommuneId is { } cm) applied.Add("Commune : " + (await db.GeographicAreas.Where(x => x.Id == cm).Select(x => x.Name).FirstOrDefaultAsync(ct) ?? "?"));
        if (f.CategoryId is { } ct2) applied.Add("Secteur : " + (await db.BusinessCategories.Where(x => x.Id == ct2).Select(x => x.Name).FirstOrDefaultAsync(ct) ?? "?"));
        return new IndicatorSet(f.From, f.To, pFrom, pTo, list, c, applied, cur.Completeness);
    }

    private static (DateTime From, DateTime To) Utc(DateOnly from, DateOnly to) =>
        (Dates.LocalToUtc(from.ToDateTime(TimeOnly.MinValue)), Dates.LocalToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue)));

    private async Task<Raw> LoadAsync(IndicatorFilter f, DateOnly from, DateOnly to, IReadOnlyList<Guid>? users, CancellationToken ct)
    {
        var (fromUtc, toUtc) = Utc(from, to);
        var vq = db.Visits.AsNoTracking().Where(v => v.ScheduledAt >= fromUtc && v.ScheduledAt < toUtc);
        if (users is not null) vq = vq.Where(v => users.Contains(v.UserId));
        if (f.CampaignId is { } c) vq = vq.Where(v => v.CampaignId == c);
        if (f.CommuneId is { } cm) vq = vq.Where(v => v.Business!.CommuneId == cm);
        if (f.CategoryId is { } ca) vq = vq.Where(v => v.Business!.CategoryId == ca);
        var visits = await vq.Take(100_000).Select(v => new V(v.BusinessId, v.UserId, v.Action, v.Status, v.WasPlanned, v.Interest,
            db.StatusValues.Where(s => s.Id == v.OutcomeStatusId).Select(s => s.Code).FirstOrDefault(), v.ScheduledAt)).ToListAsync(ct);

        // Assigned population: the campaign's targets, otherwise the businesses assigned to the selected users during the period.
        List<(Guid, Guid)> assigned;
        if (f.CampaignId is { } camp)
        {
            var tq = db.CampaignTargets.AsNoTracking().Where(t => t.CampaignId == camp && t.AssignedUserId != null);
            if (users is not null) tq = tq.Where(t => users.Contains(t.AssignedUserId!.Value));
            if (f.CommuneId is { } cm2) tq = tq.Where(t => t.Business!.CommuneId == cm2);
            if (f.CategoryId is { } ca2) tq = tq.Where(t => t.Business!.CategoryId == ca2);
            assigned = (await tq.Select(t => new { t.BusinessId, U = t.AssignedUserId!.Value }).ToListAsync(ct)).Select(x => (x.BusinessId, x.U)).ToList();
        }
        else
        {
            var aq = db.BusinessAssignments.AsNoTracking().Where(a => a.CreatedAt < toUtc && (a.IsActive || a.EndedAt == null || a.EndedAt >= fromUtc));
            if (users is not null) aq = aq.Where(a => users.Contains(a.UserId));
            if (f.CommuneId is { } cm2) aq = aq.Where(a => a.Business!.CommuneId == cm2);
            if (f.CategoryId is { } ca2) aq = aq.Where(a => a.Business!.CategoryId == ca2);
            assigned = (await aq.Select(a => new { a.BusinessId, a.UserId }).ToListAsync(ct)).Select(x => (x.BusinessId, x.UserId)).ToList();
        }

        var fq = db.FollowUps.AsNoTracking().Where(x => x.DueDate >= from && x.DueDate <= to && x.Status != FollowUpStatus.Cancelled);
        if (users is not null) fq = fq.Where(x => users.Contains(x.AssignedUserId));
        if (f.CampaignId is { } c3) fq = fq.Where(x => x.CampaignId == c3);
        if (f.CommuneId is { } cm3) fq = fq.Where(x => x.Business!.CommuneId == cm3);
        if (f.CategoryId is { } ca3) fq = fq.Where(x => x.Business!.CategoryId == ca3);
        var follow = (await fq.Select(x => new { x.DueDate, x.Status }).ToListAsync(ct)).Select(x => (x.DueDate, x.Status)).ToList();

        // Spending: outing expenses dated in the period (money is summed client-side, portable across providers).
        var eq = db.Expenses.AsNoTracking().Where(e => e.OutingId != null && e.Date >= from && e.Date <= to);
        if (f.CampaignId is { } c4) eq = eq.Where(e => e.CampaignId == c4);
        if (users is not null) eq = eq.Where(e => e.UserId != null && users.Contains(e.UserId.Value));
        var exp = await eq.Select(e => new { e.Kind, e.Amount }).ToListAsync(ct);

        var ids = assigned.Select(a => a.Item1).Distinct().Take(20_000).ToList();
        double? completeness = ids.Count == 0 ? null : await db.Businesses.AsNoTracking().Where(b => ids.Contains(b.Id)).AverageAsync(b => (double?)b.CompletenessPercent, ct);
        return new Raw(visits, assigned, follow, exp.Where(e => e.Kind == ExpenseKind.Actual).Sum(e => e.Amount), exp.Where(e => e.Kind == ExpenseKind.Planned).Sum(e => e.Amount), completeness);
    }

    private static decimal? Pct(int num, int den) => den == 0 ? null : Math.Round(num * 100m / den, 1);

    private static (Counts, List<Indicator>) Build(Raw r, DateOnly today)
    {
        var done = r.Visits.Where(v => v.Status == VisitStatus.Done).ToList();
        var doneField = done.Where(v => FieldActions.Contains(v.Action)).ToList();

        // Realisation: planned field actions (postponed ones are replaced by their successor, so they are not counted twice).
        var planned = r.Visits.Where(v => v.WasPlanned && FieldActions.Contains(v.Action) && v.Status != VisitStatus.Postponed).ToList();
        var plannedDone = planned.Count(v => v.Status == VisitStatus.Done);

        var assignedSet = r.Assigned.Select(a => a.Biz).Distinct().ToHashSet();
        var contacted = done.Select(v => v.BusinessId).Distinct().ToHashSet();
        var treated = contacted.Where(assignedSet.Contains).ToList(); // coverage only counts businesses from the assigned population

        // Interest/conversion per distinct business, from its latest recorded result in the period.
        var latest = done.GroupBy(v => v.BusinessId).Select(g => g.OrderByDescending(x => x.At).First()).ToList();
        bool IsInterested(V v) => (v.Outcome is not null && InterestedOutcomes.Contains(v.Outcome)) || v.Interest is InterestLevel.Medium or InterestLevel.High;
        var interested = latest.Count(IsInterested);
        var clients = done.Where(v => v.Outcome == "won").Select(v => v.BusinessId).Distinct().Count();

        var dueFollow = r.FollowUps.Where(f => f.Due <= today).ToList();
        var followDone = dueFollow.Count(f => f.Status == FollowUpStatus.Done);

        var costDen = doneField.Count;
        decimal? cost = costDen == 0 || r.Actual == 0 ? null : Math.Round(r.Actual / costDen, 2);

        var counts = new Counts(assignedSet.Count, treated.Count, contacted.Count, done.Count(v => v.Action == VisitAction.Call), planned.Count, doneField.Count, dueFollow.Count, followDone,
            dueFollow.Count(f => f.Status != FollowUpStatus.Done && f.Due < today), interested,
            r.Visits.Where(v => v.Action == VisitAction.Appointment && v.Status != VisitStatus.Cancelled).Select(v => v.BusinessId).Concat(done.Where(v => v.Outcome == "appointment_requested").Select(v => v.BusinessId)).Distinct().Count(),
            done.Where(v => v.Outcome == "demo_requested").Select(v => v.BusinessId).Distinct().Count(), done.Where(v => v.Outcome == "proposal_sent").Select(v => v.BusinessId).Distinct().Count(), clients,
            r.Actual, r.Planned, done.Count(v => v.Interest is not null || v.Outcome is not null));

        var list = new List<Indicator>
        {
            new("realisation", "Taux de réalisation", "visites planifiées réalisées ÷ visites planifiées (visite, rendez-vous, démonstration ; les reports sont comptés une seule fois)", plannedDone, planned.Count, Pct(plannedDone, planned.Count), "%", null, null),
            new("coverage", "Taux de couverture", "entreprises distinctes traitées ÷ entreprises distinctes affectées", treated.Count, assignedSet.Count, Pct(treated.Count, assignedSet.Count), "%", null, null),
            new("interest", "Taux d'intérêt", "prospects intéressés ÷ prospects effectivement contactés (entreprises distinctes, dernier résultat de la période)", interested, contacted.Count, Pct(interested, contacted.Count), "%", null, null),
            new("conversion", "Taux de conversion", "clients acquis ÷ prospects effectivement contactés (entreprises distinctes)", clients, contacted.Count, Pct(clients, contacted.Count), "%", null, null),
            new("followups", "Taux de réalisation des relances", "relances effectuées ÷ relances arrivées à échéance", followDone, dueFollow.Count, Pct(followDone, dueFollow.Count), "%", null, null),
            new("costpervisit", "Coût par visite", "dépenses réelles ÷ visites réalisées (non calculable sans dépense réelle saisie)", null, costDen, cost, "DA", null, null),
        };
        return (counts, list);
    }
}
