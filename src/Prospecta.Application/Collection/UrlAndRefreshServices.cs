using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Businesses;
using Prospecta.Application.Common;
using Prospecta.Application.Security;
using Prospecta.Domain.Businesses;
using Prospecta.Domain.Common;

namespace Prospecta.Application.Collection;

public sealed record UrlInspection(string Kind, Candidate Candidate, IReadOnlyList<string> Warnings);

/// <summary>"Ajouter depuis une URL publique": a pasted map link is read locally; a website page is fetched politely. The user reviews before anything is saved.</summary>
public sealed class UrlInspectionService(IPublicPageFetcher fetcher, ICurrentUser user, ConnectorQuota quota, IOptions<CollectionOptions> options, IAuditService audit, IAppDbContext db)
{
    public async Task<UrlInspection> InspectAsync(string? url, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated || !(user.HasPermission(Permissions.BusinessCreate) || user.HasPermission(Permissions.CollectionRun))) throw new ForbiddenException();
        if (string.IsNullOrWhiteSpace(url) || url.Length > 500) throw new ValidationException("Collez une URL publique (https://…).");
        url = url.Trim();
        var maps = MapsUrlParser.Parse(url);
        if (maps.Error is null)
            return new UrlInspection("maps", new Candidate { Name = maps.Name ?? string.Empty, Latitude = maps.Latitude, Longitude = maps.Longitude, SourceUrl = url, Notes = ["Coordonnées lues dans le lien ; complétez le nom, le téléphone et l'adresse à partir de la fiche."] }, []);
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Host.Contains("google.") || u.Host.EndsWith("openstreetmap.org") || u.Host.EndsWith("goo.gl")))
            throw new ValidationException(maps.Error); // a map link we could not read: do not try to scrape it

        var o = options.Value.Website;
        if (!o.Enabled) throw new ConflictException("La lecture de pages web est désactivée par l'administrateur.");
        var (ok, msg) = await quota.TryReserveAsync("website:" + user.Id, o.DailyCalls, 0, ct);
        if (!ok) throw new ConflictException(msg!);
        var page = await fetcher.FetchAsync(url, ct); // throws ConnectorException with a user-facing reason
        var c = HtmlExtractor.Extract(page.Html, page.FinalUrl);
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(c.Name)) warnings.Add("Aucun nom d'entreprise trouvé sur la page : saisissez-le.");
        if (c.Phone is null) warnings.Add("Aucun téléphone algérien trouvé sur la page.");
        audit.Record("collection.url", "Url", null, page.FinalUrl);
        await db.SaveChangesAsync(ct);
        return new UrlInspection("page", c, warnings.Concat(c.Notes).ToList());
    }
}

public sealed record Proposal(string Field, string Label, string? Current, string? Proposed, bool Blocked, string? Why);

public sealed record RefreshCheck(Guid BusinessId, string Business, string Url, IReadOnlyList<Proposal> Proposals, string? Problem);

/// <summary>
/// Re-verification against the business's own public website. Changes are proposed, never applied silently; a value confirmed by a person is never overwritten.
/// </summary>
public sealed class RefreshService(BusinessService businesses, IPublicPageFetcher fetcher, IAppDbContext db, ICurrentUser user, IAuditService audit, ConnectorQuota quota, IOptions<CollectionOptions> options, TimeProvider clock)
{
    private static readonly (string Field, string Label)[] Fields = [("Phone", "Téléphone"), ("Address", "Adresse"), ("Name", "Nom"), ("Latitude", "Latitude"), ("Longitude", "Longitude")];

    public async Task<RefreshCheck> CheckAsync(Guid businessId, CancellationToken ct = default)
    {
        if (!user.HasPermission(Permissions.BusinessEdit) || !user.HasPermission(Permissions.CollectionRun)) throw new ForbiddenException();
        var b = await businesses.Scoped().Include(x => x.Provenances).FirstOrDefaultAsync(x => x.Id == businessId, ct) ?? throw new NotFoundException();
        if (string.IsNullOrWhiteSpace(b.Website)) throw new ValidationException("Cette fiche n'a pas de site web à vérifier.");
        var (ok, msg) = await quota.TryReserveAsync("website:" + user.Id, options.Value.Website.DailyCalls, 0, ct);
        if (!ok) throw new ConflictException(msg!);

        Candidate? c = null; string? problem = null;
        try { c = HtmlExtractor.Extract((await fetcher.FetchAsync(b.Website, ct)).Html, b.Website); }
        catch (ConnectorException ex) { problem = ex.Message; }

        var proposals = new List<Proposal>();
        if (c is not null)
        {
            string? Proposed(string f) => f switch
            {
                "Phone" => c.Phone, "Address" => c.Address, "Name" => string.IsNullOrWhiteSpace(c.Name) ? null : c.Name,
                "Latitude" => c.Latitude is null ? null : BusinessRules.FormatDouble(c.Latitude), "Longitude" => c.Longitude is null ? null : BusinessRules.FormatDouble(c.Longitude), _ => null,
            };
            foreach (var (f, label) in Fields)
            {
                var proposed = Proposed(f); var current = FieldUpdater.GetValue(b, f);
                if (proposed is null) continue;
                var same = f switch { "Phone" => AlgerianPhone.Normalize(current) == AlgerianPhone.Normalize(proposed), "Name" => TextNormalizer.NormalizeName(current) == TextNormalizer.NormalizeName(proposed), "Address" => TextNormalizer.Fold(current) == TextNormalizer.Fold(proposed), _ => current == proposed };
                if (same) continue;
                var confirmed = b.Provenances.Any(p => p.Field == f && p.Origin == FieldOrigin.Confirmed) && current is not null;
                proposals.Add(new Proposal(f, label, current, proposed, confirmed, confirmed ? "Valeur confirmée par une personne : elle ne sera pas écrasée." : null));
            }
        }

        db.BusinessHistory.Add(new BusinessDataHistory { BusinessId = b.Id, Field = "WebsiteCheck", NewValue = problem is null ? $"{proposals.Count} différence(s)" : "site inaccessible", Reason = problem, Origin = FieldOrigin.External, ChangedByUserId = user.Id, CreatedAt = clock.GetUtcNow().UtcDateTime });
        audit.Record("refresh.check", "Business", b.Id, problem ?? $"proposals={proposals.Count}");
        await db.SaveChangesAsync(ct);
        return new RefreshCheck(b.Id, b.Name, b.Website!, proposals, problem);
    }

    /// <summary>Applies the chosen proposals. Values are fetched again server-side (nothing the client sends is trusted).</summary>
    public async Task<(int Applied, IReadOnlyList<string> Blocked)> ApplyAsync(Guid businessId, IReadOnlyCollection<string> fields, CancellationToken ct = default)
    {
        var check = await CheckAsync(businessId, ct);
        var b = await businesses.Scoped().Include(x => x.Provenances).Include(x => x.Sources).FirstAsync(x => x.Id == businessId, ct);
        var updater = new FieldUpdater(b, clock, user.Id, false);
        foreach (var p in check.Proposals.Where(p => fields.Contains(p.Field))) updater.Apply(p.Field, p.Proposed, FieldOrigin.External, "Actualisation depuis le site web");
        if (updater.History.Count > 0) { b.UpdatedAt = clock.GetUtcNow().UtcDateTime; b.UpdatedByUserId = user.Id; BusinessRules.Finalize(b); db.BusinessHistory.AddRange(updater.History); }
        audit.Record("refresh.apply", "Business", b.Id, string.Join(",", updater.History.Select(h => h.Field)));
        await db.SaveChangesAsync(ct);
        return (updater.History.Count, updater.BlockedFields);
    }
}
