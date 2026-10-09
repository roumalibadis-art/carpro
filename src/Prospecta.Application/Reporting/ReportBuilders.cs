using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Businesses;
using Prospecta.Application.Common;
using Prospecta.Application.Prospecting;
using Prospecta.Domain.Common;
using Prospecta.Domain.Geography;
using Prospecta.Domain.Prospecting;
using static Prospecta.Application.Reporting.Doc;

namespace Prospecta.Application.Reporting;

public sealed class ReportParameters
{
    public string? Title { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? CommuneId { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? OutingId { get; set; }
    /// <summary>Report A: scope of the study (same filters as the business list).</summary>
    public BusinessFilter? Filter { get; set; }
    public string? Objective { get; set; }
    public string? PositiveFeedback { get; set; }
    public string? Difficulties { get; set; }
    public string? Improvements { get; set; }
}

/// <summary>Builds the four report documents from stored data only; nothing is invented, gaps read "Non renseigné".</summary>
public sealed class ReportBuilders(IAppDbContext db, ICurrentUser user, BusinessService businesses, IndicatorService indicators, OutingService outings, TeamScope team, TimeProvider clock)
{
    private string Author => user.UserName ?? "?";
    private string Now => Dates.UtcToLocal(clock.GetUtcNow().UtcDateTime).ToString("dd/MM/yyyy HH:mm");

    private static string Pct(int n, int d) => d == 0 ? "—" : $"{Math.Round(n * 100.0 / d, 1)} %";

    // ===================== A — market study =====================
    public async Task<ReportDocument> MarketStudyAsync(ReportParameters p, CancellationToken ct)
    {
        var filter = p.Filter ?? new BusinessFilter();
        var q = businesses.Filtered(filter).AsNoTracking();
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(b => b.NormalizedName).ThenBy(b => b.Id).Take(500).Select(b => new
        {
            b.Id, b.Name, b.LegalName, Cat = b.Category!.Name, Sub = b.SubCategory!.Name, W = b.Wilaya!.Name, D = b.Daira!.Name, C = b.Commune!.Name, Q = b.District!.Name,
            b.Address, b.Phone, b.Website, b.GoogleMapsUrl, b.Latitude, b.Longitude, Census = b.CensusStatus!.Label, b.CompletenessPercent, b.Confidence, b.CollectedAt, b.LastVerifiedAt, b.Description,
            Demo = b.IsDemo,
        }).ToListAsync(ct);

        var all = await q.Select(b => new { b.Id, C = b.Commune!.Name, Cat = b.Category!.Name, b.NormalizedPhone, b.Website, b.Address, HasGeo = b.Latitude != null && b.Longitude != null, Census = b.CensusStatus!.Code, b.LastVerifiedAt, b.CollectedAt, b.CompletenessPercent }).ToListAsync(ct);
        var srcCounts = await q.SelectMany(b => b.Sources).GroupBy(s => s.SourceType).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var dupPairs = await db.DuplicateCandidates.CountAsync(c => c.Status == DuplicateStatus.Pending && q.Select(b => b.Id).Contains(c.BusinessAId), ct);
        var cutoff = clock.GetUtcNow().UtcDateTime.AddMonths(-6);

        string Zone() => await2(filter);
        string await2(BusinessFilter f)
        {
            var parts = new List<string>();
            if (f.WilayaId is not null) parts.Add("wilaya filtrée");
            if (f.CommuneId is not null) parts.Add("commune filtrée");
            return parts.Count == 0 ? "Toutes les zones accessibles" : string.Join(", ", parts);
        }

        var doc = new ReportDocument { Title = Or(p.Title).Replace(NotProvided, "Étude de marché locale"), Subtitle = "Étude de marché locale — recensement des entreprises" };
        doc.Meta.Add(new("Période de collecte", all.Count == 0 ? NotProvided : $"{all.Min(a => a.CollectedAt):dd/MM/yyyy} → {all.Max(a => a.CollectedAt):dd/MM/yyyy}"));
        doc.Meta.Add(new("Zone géographique", Zone()));
        doc.Meta.Add(new("Secteur d'activité", all.Select(a => a.Cat).Where(c => c is not null).Distinct().Count() is var nc and > 0 ? $"{nc} activité(s) représentée(s)" : NotProvided));
        doc.Meta.Add(new("Entreprises recensées", total.ToString()));
        doc.Meta.Add(new("Auteur", Author));
        doc.Meta.Add(new("Généré le", Now));
        doc.Footer = "Les faits collectés et les analyses générées sont séparés ; aucune estimation de marché n'est produite.";

        doc.Sections.Add(Section("Objectif et périmètre", SectionKind.Facts, P(Or(p.Objective)), Kv(("Entreprises dans le périmètre", total.ToString()), ("Filtres appliqués", Zone()))));
        doc.Sections.Add(Section("Méthodologie et sources", SectionKind.Facts,
            P("Les fiches proviennent des sources ci-dessous. Chaque donnée garde son origine (confirmée, source externe, saisie manuelle, estimée) et sa date de collecte ; une donnée absente reste « non indiquée »."),
            Table("Répartition des fiches par source", ["Source", "Fiches"], srcCounts.OrderByDescending(s => s.N).Select(s => new[] { s.Key.ToString(), s.N.ToString() }))));

        doc.Sections.Add(Section("Tableau récapitulatif des entreprises recensées", SectionKind.Facts,
            Table(total > rows.Count ? $"{rows.Count} premières fiches sur {total} (ordre alphabétique)" : $"{rows.Count} fiche(s)", ["Entreprise", "Activité", "Commune", "Téléphone", "Site web", "Recensement", "Compl."],
                rows.Select(r => new[] { r.Name + (r.Demo && !r.Name.Contains("[DÉMO]") ? " [DÉMO]" : ""), r.Sub ?? r.Cat ?? NotProvided, r.C ?? r.W ?? NotProvided, Or(r.Phone), Or(r.Website), r.Census, r.CompletenessPercent + " %" }))));

        var fiches = rows.Take(100).Select(r => Kv(
            ("Entreprise", r.Name), ("Nom légal", Or(r.LegalName)), ("Activité", $"{Or(r.Cat)}{(r.Sub is null ? "" : " › " + r.Sub)}"),
            ("Localisation", Or(string.Join(" › ", new[] { r.W, r.D, r.C, r.Q }.Where(x => x is not null)))), ("Adresse", Or(r.Address)), ("Téléphone", Or(r.Phone)), ("Site web", Or(r.Website)),
            ("Coordonnées", r.Latitude is null ? NotProvided : $"{r.Latitude:0.00000}, {r.Longitude:0.00000}"), ("Recensement / confiance", $"{r.Census} / {r.Confidence}"),
            ("Dernière vérification", r.LastVerifiedAt is null ? "jamais vérifiée" : r.LastVerifiedAt.Value.ToString("dd/MM/yyyy")), ("Collectée le", r.CollectedAt.ToString("dd/MM/yyyy")))).ToList();
        var ficheBlocks = new List<ReportBlock>();
        if (fiches.Count == 0) ficheBlocks.Add(P("Aucune fiche dans le périmètre."));
        ficheBlocks.AddRange(fiches);
        if (total > fiches.Count) ficheBlocks.Add(Note($"{fiches.Count} fiches détaillées sur {total} : les autres figurent dans le tableau récapitulatif et l'export Excel."));
        doc.Sections.Add(new ReportSection { Heading = "Fiches détaillées", Kind = SectionKind.Facts, Blocks = ficheBlocks });

        doc.Sections.Add(Section("Répartition par commune et activité", SectionKind.Facts,
            Table("Par commune", ["Commune", "Entreprises", "Part"], all.GroupBy(a => a.C ?? NotProvided).OrderByDescending(g => g.Count()).Take(30).Select(g => new[] { g.Key, g.Count().ToString(), Pct(g.Count(), total) })),
            Table("Par activité", ["Activité", "Entreprises", "Part"], all.GroupBy(a => a.Cat ?? NotProvided).OrderByDescending(g => g.Count()).Take(30).Select(g => new[] { g.Key, g.Count().ToString(), Pct(g.Count(), total) }))));

        int withPhone = all.Count(a => a.NormalizedPhone is not null), withSite = all.Count(a => a.Website is not null), withAddr = all.Count(a => a.Address is not null), withGeo = all.Count(a => a.HasGeo);
        doc.Sections.Add(Section("Coordonnées disponibles", SectionKind.Facts, Table(null, ["Information", "Disponible", "Part"], [
            ["Téléphone", withPhone.ToString(), Pct(withPhone, total)], ["Site web", withSite.ToString(), Pct(withSite, total)], ["Adresse", withAddr.ToString(), Pct(withAddr, total)], ["Coordonnées GPS", withGeo.ToString(), Pct(withGeo, total)]])));
        doc.Sections.Add(Section("Informations manquantes", SectionKind.Facts, Table(null, ["Information", "Fiches sans cette donnée", "Part"], [
            ["Téléphone", (total - withPhone).ToString(), Pct(total - withPhone, total)], ["Site web", (total - withSite).ToString(), Pct(total - withSite, total)], ["Adresse", (total - withAddr).ToString(), Pct(total - withAddr, total)],
            ["Coordonnées GPS", (total - withGeo).ToString(), Pct(total - withGeo, total)], ["Commune", all.Count(a => a.C is null).ToString(), Pct(all.Count(a => a.C is null), total)], ["Activité", all.Count(a => a.Cat is null).ToString(), Pct(all.Count(a => a.Cat is null), total)]])));
        doc.Sections.Add(Section("Doublons potentiels", SectionKind.Facts, Kv(("Couples en attente d'examen", dupPairs.ToString()), ("Entreprises marquées « doublon potentiel »", all.Count(a => a.Census == StatusCodes.PotentialDuplicate).ToString()))));
        var verified = all.Count(a => a.Census == StatusCodes.Verified);
        var stale = all.Count(a => a.LastVerifiedAt is null || a.LastVerifiedAt < cutoff);
        doc.Sections.Add(Section("État de vérification des fiches", SectionKind.Facts,
            Table(null, ["État", "Fiches", "Part"], all.GroupBy(a => a.Census).OrderByDescending(g => g.Count()).Select(g => new[] { g.Key, g.Count().ToString(), Pct(g.Count(), total) })),
            Kv(("Vérifiées", $"{verified} ({Pct(verified, total)})"), ("Jamais vérifiées ou vérifiées il y a plus de 6 mois", $"{stale} ({Pct(stale, total)})"))));

        // ---- generated reading: clearly separated from the facts above ----
        var avg = total == 0 ? 0 : all.Average(a => a.CompletenessPercent);
        var analysis = new List<string>();
        if (total == 0) analysis.Add("Aucune entreprise dans le périmètre : aucune analyse possible.");
        else
        {
            var top = all.GroupBy(a => a.C ?? NotProvided).OrderByDescending(g => g.Count()).First();
            analysis.Add($"{total} entreprise(s) recensée(s) ; la commune la plus représentée est « {top.Key} » ({Pct(top.Count(), total)}).");
            analysis.Add($"Complétude moyenne des fiches : {avg:0.#} %. Téléphone disponible pour {Pct(withPhone, total)} des fiches, site web pour {Pct(withSite, total)}.");
            analysis.Add($"{Pct(verified, total)} des fiches ont été vérifiées par une personne ; les autres reposent sur des sources non confirmées.");
        }

        doc.Sections.Add(Section("Analyse descriptive des résultats (générée)", SectionKind.Analysis, List(analysis), Note("Lecture automatique des chiffres ci-dessus : descriptive uniquement.")));
        doc.Sections.Add(Section("Limites de l'étude", SectionKind.Analysis, List([
            "Le recensement reflète les sources consultées à la date de collecte ; il n'est pas exhaustif et peut contenir des entreprises fermées ou des doublons non encore détectés.",
            "Les données issues de sources externes ne sont pas confirmées tant qu'une personne ne les a pas vérifiées.",
            "L'étude décrit l'offre recensée ; elle ne mesure ni la demande, ni le chiffre d'affaires, ni les parts de marché.",
            "Les coordonnées GPS et adresses manquantes limitent la cartographie.",
        ])));
        var reco = new List<string>();
        if (total > 0)
        {
            if (total - withPhone > total * 0.3) reco.Add($"Compléter les numéros de téléphone : {Pct(total - withPhone, total)} des fiches n'en ont pas.");
            if (stale > total * 0.3) reco.Add($"Planifier une vérification : {stale} fiche(s) n'ont jamais été vérifiées ou datent de plus de 6 mois.");
            if (dupPairs > 0) reco.Add($"Examiner les {dupPairs} couple(s) de doublons potentiels avant toute prospection.");
            if (total - withGeo > total * 0.5) reco.Add("Renseigner les coordonnées GPS pour exploiter la carte des entreprises.");
        }

        if (reco.Count == 0) reco.Add("Aucune action prioritaire déduite des données actuelles.");
        doc.Sections.Add(Section("Conclusions et recommandations (générées)", SectionKind.Analysis, List(reco),
            Note("Recommandations déduites automatiquement de règles simples sur la qualité des données. Aucune conclusion sur la taille du marché, le chiffre d'affaires, les parts de marché ou la demande n'est formulée : les données collectées ne permettent pas de les calculer.")));
        doc.Sections.Add(Section("Génération", SectionKind.Facts, Kv(("Généré le", Now), ("Auteur", Author))));
        return doc;
    }

    // ===================== B — outing balance =====================
    public async Task<ReportDocument> OutingBalanceAsync(ReportParameters p, CancellationToken ct)
    {
        if (p.OutingId is not { } oid) throw new ValidationException("Choisissez la sortie à bilanter.");
        var o = await outings.GetAsync(oid, ct); // throws NotFound when the caller cannot see the outing
        var visits = await db.Visits.AsNoTracking().Where(v => v.OutingId == oid).OrderBy(v => v.ScheduledAt).Select(v => new
        {
            v.BusinessId, Biz = v.Business!.Name, v.Action, v.Status, v.WasPlanned, v.ContactMet, v.Interest, v.Objections, v.RequestedInfo, v.NextAction, v.NextFollowUpDate, v.Comment,
            User = db.AppUsers.Where(u => u.Id == v.UserId).Select(u => u.FullName).FirstOrDefault(), Outcome = db.StatusValues.Where(s => s.Id == v.OutcomeStatusId).Select(s => s.Code).FirstOrDefault(),
            Label = db.StatusValues.Where(s => s.Id == v.OutcomeStatusId).Select(s => s.Label).FirstOrDefault(),
        }).ToListAsync(ct);
        var done = visits.Where(v => v.Status == VisitStatus.Done).ToList();
        var interestedSet = new[] { "interested", "appointment_requested", "demo_requested", "proposal_sent", "negotiation", "won" };
        var interested = done.Where(v => (v.Outcome is not null && interestedSet.Contains(v.Outcome)) || v.Interest is InterestLevel.Medium or InterestLevel.High).GroupBy(v => v.BusinessId).Select(g => g.First()).ToList();
        var d = o.Data;

        var doc = new ReportDocument { Title = Or(p.Title).Replace(NotProvided, $"Bilan de sortie commerciale du {d.Date:dd/MM/yyyy}"), Subtitle = "Bilan de sortie commerciale" };
        doc.Meta.Add(new("Date de la sortie", d.Date.ToString("dd/MM/yyyy")));
        doc.Meta.Add(new("Heure de départ", d.DepartureTime?.ToString("HH:mm") ?? NotProvided));
        doc.Meta.Add(new("Durée", d.DurationMinutes is null ? NotProvided : d.DurationMinutes + " min"));
        doc.Meta.Add(new("Responsable(s)", o.Manager + (o.Participants.Count > 0 ? " ; participants : " + string.Join(", ", o.Participants.Select(x => x.Name)) : "")));
        doc.Meta.Add(new("Point de départ", Or(d.StartPoint)));
        doc.Meta.Add(new("Zone visitée", Or(d.Zone)));
        doc.Meta.Add(new("Campagne", Or(o.Campaign)));
        doc.Meta.Add(new("Auteur", Author));
        doc.Meta.Add(new("Généré le", Now));

        doc.Sections.Add(Section("Synthèse des indicateurs", SectionKind.Facts, Table(null, ["Indicateur", "Valeur"], [
            ["Visites planifiées", o.VisitsPlanned.ToString()], ["Visites réalisées", o.VisitsDone.ToString()], ["Annulées / reportées", $"{o.VisitsCancelled} / {o.VisitsPostponed}"],
            ["Taux de réalisation", o.VisitsPlanned == 0 ? "Non calculable" : Pct(visits.Count(v => v.WasPlanned && v.Status == VisitStatus.Done), visits.Count(v => v.WasPlanned && v.Status != VisitStatus.Postponed))],
            ["Prospects intéressés", interested.Count.ToString()], ["Démonstrations ou informations demandées", done.Count(v => v.Outcome == "demo_requested" || !string.IsNullOrWhiteSpace(v.RequestedInfo)).ToString()],
            ["Conversions (clients acquis)", done.Where(v => v.Outcome == "won").Select(v => v.BusinessId).Distinct().Count().ToString()]])));
        doc.Sections.Add(Section("Responsables rencontrés", SectionKind.Facts, done.Any(v => !string.IsNullOrWhiteSpace(v.ContactMet))
            ? Table(null, ["Entreprise", "Responsable rencontré"], done.Where(v => !string.IsNullOrWhiteSpace(v.ContactMet)).Select(v => new[] { v.Biz, v.ContactMet! })) : P(NotProvided)));
        doc.Sections.Add(Section("Prospects intéressés", SectionKind.Facts, interested.Count == 0 ? P("Aucun prospect intéressé enregistré.") : Table(null, ["Entreprise", "Intérêt", "Résultat"], interested.Select(v => new[] { v.Biz, v.Interest?.ToString() ?? NotProvided, v.Label ?? NotProvided }))));
        var asked = done.Where(v => v.Outcome == "demo_requested" || !string.IsNullOrWhiteSpace(v.RequestedInfo)).ToList();
        doc.Sections.Add(Section("Démonstrations ou informations demandées", SectionKind.Facts, asked.Count == 0 ? P(NotProvided) : Table(null, ["Entreprise", "Demande"], asked.Select(v => new[] { v.Biz, Or(v.RequestedInfo ?? v.Label) }))));
        var won = done.Where(v => v.Outcome == "won").ToList();
        doc.Sections.Add(Section("Inscriptions, engagements ou conversions", SectionKind.Facts, won.Count == 0 ? P("Aucune conversion enregistrée.") : Table(null, ["Entreprise", "Par"], won.Select(v => new[] { v.Biz, v.User ?? "?" }))));
        var todo = visits.Where(v => v.NextFollowUpDate is not null).ToList();
        doc.Sections.Add(Section("Relances à effectuer", SectionKind.Facts, todo.Count == 0 ? P(NotProvided) : Table(null, ["Entreprise", "Échéance", "Action prévue"], todo.OrderBy(v => v.NextFollowUpDate).Select(v => new[] { v.Biz, v.NextFollowUpDate!.Value.ToString("dd/MM/yyyy"), Or(v.NextAction) }))));
        doc.Sections.Add(Section("Résultats par prospect", SectionKind.Facts, visits.Count == 0 ? P("Aucune visite rattachée à cette sortie.")
            : Table(null, ["Entreprise", "Action", "Par", "État", "Responsable", "Intérêt", "Résultat"], visits.Select(v => new[] { v.Biz, v.Action.ToString(), v.User ?? "?", v.Status.ToString(), Or(v.ContactMet), v.Interest?.ToString() ?? NotProvided, v.Label ?? NotProvided }))));

        string Cat(ExpenseCategory c) => o.ActualByCategory.TryGetValue(c, out var v) ? v.ToString("N2") + " DA" : "0,00 DA";
        doc.Sections.Add(Section("Dépenses", SectionKind.Facts,
            Table(null, ["Poste", "Réel"], [["Transport", Cat(ExpenseCategory.Transport)], ["Supports marketing", Cat(ExpenseCategory.Marketing)], ["Autres frais", Cat(ExpenseCategory.Other)]]),
            Kv(("Total prévisionnel", o.PlannedTotal.ToString("N2") + " DA"), ("Total réel", o.ActualTotal.ToString("N2") + " DA"))));

        doc.Sections.Add(Section("Retours positifs", SectionKind.Facts, P(Or(p.PositiveFeedback))));
        var obj = done.Where(v => !string.IsNullOrWhiteSpace(v.Objections)).GroupBy(v => v.Objections!.Trim().ToLowerInvariant()).OrderByDescending(g => g.Count()).Select(g => new[] { g.First().Objections!.Trim(), g.Count().ToString() }).ToList();
        doc.Sections.Add(Section("Questions et objections fréquentes", SectionKind.Facts, obj.Count == 0 ? P(NotProvided) : Table(null, ["Question / objection", "Occurrences"], obj)));
        doc.Sections.Add(Section("Difficultés rencontrées", SectionKind.Facts, P(Or(p.Difficulties ?? d.Observations))));
        doc.Sections.Add(Section("Améliorations proposées", SectionKind.Facts, P(Or(p.Improvements))));
        doc.Sections.Add(Section("Observations générales de la sortie", SectionKind.Facts, P(Or(d.Observations))));
        doc.Footer = "Auteur, vérificateur et date de validation : voir l'en-tête du rapport enregistré (la validation est ajoutée par un responsable).";
        return doc;
    }

    // ===================== C — individual =====================
    public async Task<ReportDocument> IndividualAsync(ReportParameters p, CancellationToken ct)
    {
        var (from, to) = Period(p);
        var uid = p.UserId ?? user.Id!.Value;
        var set = await indicators.ComputeAsync(new IndicatorFilter { From = from, To = to, CampaignId = p.CampaignId, UserId = uid, CommuneId = p.CommuneId, CategoryId = p.CategoryId }, ct);
        var name = await db.AppUsers.Where(u => u.Id == uid).Select(u => u.FullName).FirstOrDefaultAsync(ct) ?? "?";
        var c = set.Counts;
        var (fromUtc, toUtc) = (Dates.LocalToUtc(from.ToDateTime(TimeOnly.MinValue)), Dates.LocalToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue)));
        var comments = await db.Visits.AsNoTracking().Where(v => v.UserId == uid && v.Status == VisitStatus.Done && v.ScheduledAt >= fromUtc && v.ScheduledAt < toUtc && v.Comment != null)
            .OrderByDescending(v => v.ScheduledAt).Take(15).Select(v => new { v.ScheduledAt, Biz = v.Business!.Name, v.Comment }).ToListAsync(ct);
        var today = Dates.Today(clock);
        var pendingVisits = await db.Visits.AsNoTracking().Where(v => v.UserId == uid && v.Status == VisitStatus.Planned).OrderBy(v => v.ScheduledAt).Take(30).Select(v => new { v.ScheduledAt, v.Action, Biz = v.Business!.Name }).ToListAsync(ct);
        var pendingFollow = await db.FollowUps.AsNoTracking().Where(f => f.AssignedUserId == uid && f.Status == FollowUpStatus.ToDo).OrderBy(f => f.DueDate).Take(30).Select(f => new { f.DueDate, f.Reason, Biz = f.Business!.Name }).ToListAsync(ct);

        var doc = new ReportDocument { Title = Or(p.Title).Replace(NotProvided, $"Rapport individuel — {name}"), Subtitle = "Rapport individuel du commercial" };
        doc.Meta.Add(new("Commercial", name)); doc.Meta.Add(new("Période", $"{from:dd/MM/yyyy} → {to:dd/MM/yyyy}"));
        foreach (var f in set.Filters.Skip(1).Where(x => !x.StartsWith("Utilisateur"))) { var i = f.IndexOf(" : ", StringComparison.Ordinal); if (i > 0) doc.Meta.Add(new(f[..i], f[(i + 3)..])); }
        doc.Meta.Add(new("Auteur", Author)); doc.Meta.Add(new("Généré le", Now));

        doc.Sections.Add(Section("Activité de la période", SectionKind.Facts, Table(null, ["Indicateur", "Valeur"], [
            ["Prospects affectés", c.Assigned.ToString()], ["Prospects traités (entreprises distinctes)", c.Treated.ToString()], ["Prospects contactés (entreprises distinctes)", c.Contacted.ToString()], ["Appels réalisés", c.Calls.ToString()],
            ["Visites planifiées", c.VisitsPlanned.ToString()], ["Visites réalisées", c.VisitsDone.ToString()], ["Relances arrivées à échéance", c.FollowUpsDue.ToString()], ["Relances effectuées", c.FollowUpsDone.ToString()], ["Relances en retard", c.FollowUpsOverdue.ToString()],
            ["Prospects intéressés", c.Interested.ToString()], ["Rendez-vous obtenus", c.Appointments.ToString()], ["Démonstrations demandées", c.DemosRequested.ToString()], ["Propositions envoyées", c.ProposalsSent.ToString()], ["Clients acquis", c.Clients.ToString()],
            ["Dépenses réelles", c.ExpensesActual.ToString("N2") + " DA"]])));
        doc.Sections.Add(Section("Indicateurs de performance", SectionKind.Facts, IndicatorTable(set),
            Note($"Évolution par rapport à la période comparable ({set.PreviousFrom:dd/MM/yyyy} → {set.PreviousTo:dd/MM/yyyy}). « Non calculable » = données ou dénominateur manquants ; ce n'est pas un mauvais résultat. Ces chiffres ne suffisent pas à juger la qualité du travail : tenir compte du contexte, des objectifs et de la qualité des données.")));
        doc.Sections.Add(Section("Actions en attente", SectionKind.Facts,
            pendingVisits.Count == 0 ? P("Aucune action planifiée en attente.") : Table("Actions planifiées", ["Date prévue", "Action", "Entreprise"], pendingVisits.Select(v => new[] { Dates.UtcToLocal(v.ScheduledAt).ToString("dd/MM/yyyy HH:mm"), v.Action.ToString(), v.Biz })),
            pendingFollow.Count == 0 ? P("Aucune relance à faire.") : Table("Relances à faire", ["Échéance", "Entreprise", "Motif"], pendingFollow.Select(f => new[] { f.DueDate.ToString("dd/MM/yyyy") + (f.DueDate < today ? " (en retard)" : ""), f.Biz, f.Reason }))));
        doc.Sections.Add(Section("Résumé des observations", SectionKind.Facts, comments.Count == 0 ? P(NotProvided) : Table(null, ["Date", "Entreprise", "Observation"], comments.Select(x => new[] { Dates.UtcToLocal(x.ScheduledAt).ToString("dd/MM/yyyy"), x.Biz, x.Comment! }))));

        var reco = new List<string>();
        if (c.FollowUpsOverdue > 0) reco.Add($"Traiter en priorité les {c.FollowUpsOverdue} relance(s) en retard.");
        if (c.Assigned > 0 && c.Treated < c.Assigned) reco.Add($"{c.Assigned - c.Treated} prospect(s) affecté(s) n'ont pas encore été traités sur la période.");
        if (c.Contacted > 0 && c.VisitsWithResult < c.Contacted) reco.Add("Renseigner le niveau d'intérêt ou le résultat de chaque action pour fiabiliser les indicateurs.");
        if (reco.Count == 0) reco.Add("Aucune recommandation déduite des données de la période.");
        doc.Sections.Add(Section("Recommandations pour la période suivante (générées)", SectionKind.Analysis, List(reco), Note("Déduites automatiquement ; à confronter au contexte de terrain.")));
        return doc;
    }

    // ===================== D — manager =====================
    public async Task<ReportDocument> ManagerAsync(ReportParameters p, CancellationToken ct)
    {
        if (!user.HasPermission(Prospecta.Application.Security.Permissions.ReportViewTeam)) throw new ForbiddenException();
        var (from, to) = Period(p);
        var scope = await team.UserIdsAsync(ct);
        var usersQ = db.AppUsers.AsNoTracking().Where(u => u.IsActive);
        if (scope is not null) usersQ = usersQ.Where(u => scope.Contains(u.Id));
        var members = await usersQ.OrderBy(u => u.FullName).Take(50).Select(u => new { u.Id, u.FullName }).ToListAsync(ct);
        var total = await indicators.ComputeAsync(new IndicatorFilter { From = from, To = to, CampaignId = p.CampaignId, CommuneId = p.CommuneId, CategoryId = p.CategoryId }, ct);

        string Cell(Indicator i) => i.Computable ? $"{i.Display} ({i.Numerator}/{i.Denominator})" : "Non calculable";
        string Small(Indicator i) => i.Denominator is < 5 and > 0 ? " ⚠ échantillon faible" : "";

        var perUser = new List<string[]>();
        foreach (var m in members)
        {
            var s = await indicators.ComputeAsync(new IndicatorFilter { From = from, To = to, CampaignId = p.CampaignId, UserId = m.Id, CommuneId = p.CommuneId, CategoryId = p.CategoryId }, ct);
            if (s.Counts.Assigned == 0 && s.Counts.Contacted == 0 && s.Counts.VisitsPlanned == 0) continue;
            perUser.Add([m.FullName, s.Counts.Assigned.ToString(), s.Counts.Contacted.ToString(), Cell(s.Indicators[0]) + Small(s.Indicators[0]), Cell(s.Indicators[1]), Cell(s.Indicators[2]), Cell(s.Indicators[3]), Cell(s.Indicators[4]), s.Counts.FollowUpsOverdue.ToString()]);
        }

        var (fromUtc, toUtc) = (Dates.LocalToUtc(from.ToDateTime(TimeOnly.MinValue)), Dates.LocalToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue)));
        var camps = await db.Campaigns.AsNoTracking().Where(c => c.StartDate <= to && c.EndDate >= from && c.Status != CampaignStatus.Cancelled).OrderBy(c => c.Name).Take(15).Select(c => new { c.Id, c.Name, c.VisitTarget, c.Budget }).ToListAsync(ct);
        var perCampaign = new List<string[]>();
        foreach (var c in camps)
        {
            var s = await indicators.ComputeAsync(new IndicatorFilter { From = from, To = to, CampaignId = c.Id, CommuneId = p.CommuneId, CategoryId = p.CategoryId }, ct);
            var gap = c.VisitTarget is null ? NotProvided : $"{s.Counts.VisitsDone} / {c.VisitTarget} ({(s.Counts.VisitsDone - c.VisitTarget >= 0 ? "+" : "")}{s.Counts.VisitsDone - c.VisitTarget})";
            perCampaign.Add([c.Name, gap, s.Counts.Treated + " / " + s.Counts.Assigned, Cell(s.Indicators[1]), Cell(s.Indicators[2]), s.Counts.ExpensesActual.ToString("N2") + (c.Budget is null ? "" : " / " + c.Budget.Value.ToString("N2"))]);
        }

        // Zones: communes with assigned businesses; coverage computed with the same definition as everywhere else.
        var communeIds = await db.BusinessAssignments.AsNoTracking().Where(a => a.IsActive && (scope == null || scope.Contains(a.UserId)) && a.Business!.CommuneId != null)
            .GroupBy(a => a.Business!.CommuneId!.Value).OrderByDescending(g => g.Count()).Take(25).Select(g => g.Key).ToListAsync(ct);
        var communeNames = await db.GeographicAreas.Where(a => communeIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var perCommune = new List<string[]>(); var under = new List<string>();
        foreach (var cid in communeIds)
        {
            var s = await indicators.ComputeAsync(new IndicatorFilter { From = from, To = to, CampaignId = p.CampaignId, CommuneId = cid, CategoryId = p.CategoryId }, ct);
            perCommune.Add([communeNames.GetValueOrDefault(cid, "?"), s.Counts.Assigned.ToString(), s.Counts.Treated.ToString(), Cell(s.Indicators[1]), Cell(s.Indicators[2])]);
            if (s.Indicators[1].Value is < 50 && s.Counts.Assigned > 0) under.Add($"{communeNames.GetValueOrDefault(cid, "?")} : couverture {s.Indicators[1].Display} ({s.Counts.Treated}/{s.Counts.Assigned})");
        }

        var cats = await db.BusinessAssignments.AsNoTracking().Where(a => a.IsActive && (scope == null || scope.Contains(a.UserId)) && a.Business!.CategoryId != null).GroupBy(a => a.Business!.CategoryId!.Value).OrderByDescending(g => g.Count()).Take(15).Select(g => g.Key).ToListAsync(ct);
        var catNames = await db.BusinessCategories.Where(c => cats.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var perCat = new List<string[]>();
        foreach (var cid in cats)
        {
            var s = await indicators.ComputeAsync(new IndicatorFilter { From = from, To = to, CampaignId = p.CampaignId, CategoryId = cid, CommuneId = p.CommuneId }, ct);
            perCat.Add([catNames.GetValueOrDefault(cid, "?"), s.Counts.Assigned.ToString(), Cell(s.Indicators[1]), Cell(s.Indicators[2]), Cell(s.Indicators[3])]);
        }

        var pendingProspects = await db.BusinessAssignments.AsNoTracking().Where(a => a.IsActive && (scope == null || scope.Contains(a.UserId)))
            .Where(a => !db.Visits.Any(v => v.BusinessId == a.BusinessId && v.Status == VisitStatus.Done && v.ScheduledAt >= fromUtc && v.ScheduledAt < toUtc)).Select(a => a.BusinessId).Distinct().CountAsync(ct);
        var today = Dates.Today(clock);
        var overdue = await db.FollowUps.AsNoTracking().Where(f => f.Status == FollowUpStatus.ToDo && f.DueDate < today && (scope == null || scope.Contains(f.AssignedUserId)))
            .GroupBy(f => f.AssignedUserId).Select(g => new { U = g.Key, N = g.Count() }).ToListAsync(ct);
        var nameMap = await db.AppUsers.Where(u => overdue.Select(o => o.U).Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        var doc = new ReportDocument { Title = Or(p.Title).Replace(NotProvided, "Rapport du responsable — comparaison"), Subtitle = "Comparaison des commerciaux, campagnes, communes et secteurs" };
        doc.Meta.Add(new("Période", $"{from:dd/MM/yyyy} → {to:dd/MM/yyyy}"));
        foreach (var f in total.Filters.Skip(1).Where(x => !x.StartsWith("Utilisateur"))) { var i = f.IndexOf(" : ", StringComparison.Ordinal); if (i > 0) doc.Meta.Add(new(f[..i], f[(i + 3)..])); }
        doc.Meta.Add(new("Périmètre", scope is null ? "Toute l'organisation" : $"{scope.Count} personne(s) de mon équipe")); doc.Meta.Add(new("Auteur", Author)); doc.Meta.Add(new("Généré le", Now));
        doc.Footer = "Comparaisons faites sur la même période, avec les mêmes définitions d'indicateurs et des populations comparables ; le numérateur/dénominateur figure entre parenthèses.";

        doc.Sections.Add(Section("Indicateurs globaux", SectionKind.Facts, IndicatorTable(total), Note("Mêmes définitions pour toutes les lignes ci-dessous. Un taux sur moins de 5 entreprises ou visites est signalé « échantillon faible » et ne doit pas servir à classer.")));
        doc.Sections.Add(Section("Comparaison des commerciaux", SectionKind.Facts, perUser.Count == 0 ? P("Aucune activité ni affectation sur la période.")
            : Table(null, ["Commercial", "Affectées", "Contactées", "Réalisation visites", "Couverture", "Intérêt", "Conversion", "Relances", "Relances en retard"], perUser)));
        doc.Sections.Add(Section("Objectifs et résultats par campagne", SectionKind.Facts, perCampaign.Count == 0 ? P("Aucune campagne active sur la période.")
            : Table("Visites réalisées / objectif (écart)", ["Campagne", "Visites / objectif", "Traitées / affectées", "Couverture", "Intérêt", "Dépenses / budget (DA)"], perCampaign)));
        doc.Sections.Add(Section("Comparaison par commune", SectionKind.Facts, perCommune.Count == 0 ? P(NotProvided) : Table(null, ["Commune", "Affectées", "Traitées", "Couverture", "Intérêt"], perCommune)));
        doc.Sections.Add(Section("Comparaison par secteur", SectionKind.Facts, perCat.Count == 0 ? P(NotProvided) : Table(null, ["Secteur", "Affectées", "Couverture", "Intérêt", "Conversion"], perCat)));
        doc.Sections.Add(Section("Prospects en attente et relances en retard", SectionKind.Facts,
            Kv(("Entreprises affectées sans action réalisée sur la période", pendingProspects.ToString())),
            overdue.Count == 0 ? P("Aucune relance en retard.") : Table("Relances en retard par personne", ["Commercial", "Relances en retard"], overdue.OrderByDescending(o => o.N).Select(o => new[] { nameMap.GetValueOrDefault(o.U, "?"), o.N.ToString() }))));

        var actions = new List<string>();
        actions.AddRange(under.Select(u => "Renforcer la couverture — " + u));
        actions.AddRange(overdue.OrderByDescending(o => o.N).Take(5).Select(o => $"Faire traiter les {o.N} relance(s) en retard de {nameMap.GetValueOrDefault(o.U, "?")}"));
        if (pendingProspects > 0) actions.Add($"Planifier une première action pour les {pendingProspects} entreprise(s) affectée(s) sans action sur la période.");
        if (actions.Count == 0) actions.Add("Aucune action prioritaire déduite des données de la période.");
        doc.Sections.Add(Section("Zones insuffisamment couvertes et actions prioritaires (générées)", SectionKind.Analysis, List(actions), Note("Seuil de couverture insuffisante : moins de 50 % des entreprises affectées traitées. Lecture automatique, à confronter aux objectifs et au contexte.")));
        return doc;
    }

    private static (DateOnly From, DateOnly To) Period(ReportParameters p)
    {
        if (p.From is not { } f || p.To is not { } t) throw new ValidationException("Choisissez la période du rapport.");
        if (t < f) throw new ValidationException("La fin de période précède le début.");
        return (f, t);
    }

    private static ReportBlock IndicatorTable(IndicatorSet s) => Table(null, ["Indicateur", "Valeur", "Numérateur / dénominateur", "Période précédente", "Évolution", "Définition"],
        s.Indicators.Select(i => new[] { i.Label, i.Display, i.Numerator is null ? "—" : $"{i.Numerator} / {i.Denominator}", i.PreviousValue is null ? "Non calculable" : (i.Unit == "%" ? $"{i.PreviousValue:0.#} %" : $"{i.PreviousValue:N2} {i.Unit}"),
            i.Delta is null ? "—" : $"{(i.Delta >= 0 ? "+" : "")}{i.Delta:0.#}{(i.Unit == "%" ? " pts" : " DA")}", i.Definition }));
}
