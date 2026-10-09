using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Businesses;
using Prospecta.Application.Common;
using Prospecta.Application.Duplicates;
using Prospecta.Application.Security;
using Prospecta.Domain.Common;
using Prospecta.Domain.Geography;
using Prospecta.Domain.Prospecting;

namespace Prospecta.Application.Collection;

public enum ConnectorState { Available, Disabled, FileOnly, NotIntegrated }

public sealed record ConnectorInfo(string Key, string Name, ConnectorState State, string Description, string Setup, string? Quota);

public sealed class CollectionRequest
{
    public string ConnectorKey { get; set; } = "osm";
    public Guid AreaId { get; set; }
    public Guid? CategoryId { get; set; }
    public string? Keywords { get; set; }
    public int MaxResults { get; set; } = 100;
    /// <summary>Optional precise search box "south,west,north,east" (decimal degrees): replaces the administrative area.</summary>
    public string? Bbox { get; set; }
}

public sealed record JobDto(Guid Id, string Connector, string User, DateTime StartedAt, DateTime? FinishedAt, CollectionJobStatus Status, int Found, int NewCount, int DuplicateCount, int ImportedCount, int Skipped, string? Message, string Summary, Guid? RetryOf);
public sealed record ResultDto(Guid Id, string ExternalId, string Name, CollectionResultStatus Status, Candidate Candidate, string? MatchReason, Guid? MatchBusinessId, Guid? BusinessId);

internal sealed class ResultPayload
{
    public Candidate Candidate { get; set; } = new();
    public Guid? WilayaId { get; set; }
    public Guid? DairaId { get; set; }
    public Guid? CommuneId { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? SubCategoryId { get; set; }
}

public sealed class ConnectorQuota(IAppDbContext db, TimeProvider clock)
{
    /// <summary>Reserves one call for today, honouring the daily quota and the minimum delay between calls.</summary>
    public async Task<(bool Ok, string? Message)> TryReserveAsync(string key, int dailyLimit, int minSeconds, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime; var day = DateOnly.FromDateTime(now);
        var u = await db.ConnectorUsage.FirstOrDefaultAsync(x => x.ConnectorKey == key && x.Day == day, ct);
        if (u is null) { u = new ConnectorUsage { ConnectorKey = key, Day = day }; db.ConnectorUsage.Add(u); }
        if (u.Calls >= dailyLimit) return (false, $"Quota quotidien atteint ({dailyLimit} requêtes). Réessayez demain ou importez un fichier Excel.");
        if (u.LastCallAt is { } last && (now - last).TotalSeconds < minSeconds) return (false, $"Respect de la limite de débit : attendez {Math.Ceiling(minSeconds - (now - last).TotalSeconds)} s avant la prochaine requête.");
        u.Calls++; u.LastCallAt = now;
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<int> RemainingAsync(string key, int dailyLimit, CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        return Math.Max(0, dailyLimit - await db.ConnectorUsage.Where(x => x.ConnectorKey == key && x.Day == day).Select(x => x.Calls).FirstOrDefaultAsync(ct));
    }
}

/// <summary>
/// Free, keyless collection. Only OpenStreetMap (Overpass) is queried live; everything else is a local parser or a file template.
/// Paid/keyed APIs (Google Places, Meta) are deliberately not integrated and reported as such.
/// </summary>
public sealed class CollectionService(
    IAppDbContext db, ICurrentUser user, IOverpassClient overpass, BusinessService businesses, DuplicateService duplicates, ConnectorQuota quota,
    IAuditService audit, IOptions<CollectionOptions> options, TimeProvider clock)
{
    private CollectionOptions Opt => options.Value;

    private void RequireRun()
    {
        if (!user.IsAuthenticated || !user.HasPermission(Permissions.CollectionRun)) throw new ForbiddenException();
    }

    public async Task<IReadOnlyList<ConnectorInfo>> ConnectorsAsync(CancellationToken ct = default)
    {
        RequireRun();
        var left = await quota.RemainingAsync("osm", Opt.Osm.DailyCalls, ct);
        return
        [
            new("osm", "OpenStreetMap (Overpass)", Opt.Osm.Enabled ? ConnectorState.Available : ConnectorState.Disabled,
                "Entreprises cartographiées par la communauté OpenStreetMap, recherchées par zone et activité. Gratuit, sans clé. Données sous licence ODbL (© contributeurs OpenStreetMap) : la source est conservée sur chaque fiche. Couverture variable en Algérie : toujours à vérifier.",
                "Aucune configuration requise. Réglages facultatifs : Collection:Osm:* (quota, délai, User-Agent avec un contact).", $"{left}/{Opt.Osm.DailyCalls} requêtes restantes aujourd'hui"),
            new("website", "Site web public d'une entreprise", Opt.Website.Enabled ? ConnectorState.Available : ConnectorState.Disabled,
                "Lit une page publique (données structurées schema.org, balises meta, liens tel:) après contrôle de robots.txt. Aucune clé ; n'accède pas aux pages protégées par CAPTCHA ou connexion.", "Aucune configuration requise.", null),
            new("maps-link", "Lien Google Maps / OpenStreetMap collé", ConnectorState.Available,
                "Extrait localement les coordonnées et le nom d'un lien copié depuis le navigateur. Aucune requête vers Google : rien à configurer, aucun quota.", "Aucune configuration requise.", null),
            new("file", "Fichier Excel / CSV", ConnectorState.FileOnly,
                "Voie de repli pour toute source sans API gratuite : remplissez le modèle fourni puis importez-le (aperçu, validation, doublons).", "Téléchargez le modèle depuis la page Imports.", null),
            new("manual", "Saisie manuelle", ConnectorState.Available, "Création d'une fiche à la main.", "—", null),
            new("google-places", "Google Maps / Places (API)", ConnectorState.NotIntegrated,
                "Non intégré : l'API Google Places est payante et exige une clé. Aucune extraction automatisée de Google Maps n'est faite (contraire aux conditions d'utilisation).",
                "Alternative gratuite : relever les fiches à la main dans Google Maps, coller le lien de chaque lieu (coordonnées extraites localement) ou remplir le modèle Excel.", null),
            new("meta", "Facebook / Meta", ConnectorState.NotIntegrated,
                "Non intégré : aucune API gratuite et sans validation d'application ; aucun accès aux groupes privés ni aux données personnelles.", "Alternative : saisir les pages publiques dans le modèle Excel.", null),
        ];
    }

    // ---------- Run ----------
    public async Task<JobDto> SearchAsync(CollectionRequest r, Guid? retryOf = null, CancellationToken ct = default)
    {
        RequireRun();
        if (r.ConnectorKey != "osm") throw new ValidationException("Seul le connecteur OpenStreetMap lance des recherches ; les autres sources passent par l'import de fichier ou l'URL.");
        if (!Opt.Osm.Enabled) throw new ConflictException("Le connecteur OpenStreetMap est désactivé par l'administrateur.");
        var errors = new List<string>();
        var area = await db.GeographicAreas.AsNoTracking().FirstOrDefaultAsync(a => a.Id == r.AreaId && a.IsActive, ct);
        if (area is null) errors.Add("Choisissez une zone (wilaya, daïra ou commune).");
        BusinessCategory? cat = r.CategoryId is { } cid ? await db.BusinessCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;
        if (cat is null) errors.Add("Choisissez une activité.");
        var filters = new List<string>();
        if (cat is not null)
        {
            filters.AddRange(OverpassQuery.ParseFilters(cat.OsmFilter));
            if (filters.Count == 0 && cat.ParentId is { } pid) filters.AddRange(OverpassQuery.ParseFilters(await db.BusinessCategories.Where(c => c.Id == pid).Select(c => c.OsmFilter).FirstOrDefaultAsync(ct)));
            if (filters.Count == 0) errors.Add($"L'activité « {cat.Name} » n'a pas de correspondance OpenStreetMap : l'administrateur peut la définir dans Activités, ou utilisez le modèle Excel.");
        }

        (double S, double W, double N, double E)? bbox = null;
        if (!string.IsNullOrWhiteSpace(r.Bbox))
        {
            var p = r.Bbox.Split(',').Select(x => double.TryParse(x.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
            if (p.Length != 4 || p.Any(double.IsNaN) || !BusinessRules.CoordinatesPlausible(p[0], p[1]) || !BusinessRules.CoordinatesPlausible(p[2], p[3]) || p[0] >= p[2] || p[1] >= p[3] || (p[2] - p[0]) > 2 || (p[3] - p[1]) > 2)
                errors.Add("Boîte invalide : « sud,ouest,nord,est » en degrés décimaux, en Algérie, de 2° maximum de côté.");
            else bbox = (p[0], p[1], p[2], p[3]);
        }

        if (errors.Count > 0) throw new ValidationException(errors);

        var now = clock.GetUtcNow().UtcDateTime;
        var max = Math.Clamp(r.MaxResults, 1, Opt.Osm.MaxResults);
        var job = new DataCollectionJob { ConnectorKey = "osm", UserId = user.Id!.Value, CreatedAt = now, ParametersJson = JsonSerializer.Serialize(r), RetryOfJobId = retryOf };
        db.CollectionJobs.Add(job);
        await db.SaveChangesAsync(ct); // the attempt is logged whatever happens next

        try
        {
            var areas = await ResolveAreasAsync(area!, ct);
            var keywords = (r.Keywords ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(k => k.Length is > 1 and < 40).ToList();
            var ql = OverpassQuery.Build(areas.Select(a => (a.Name, a.Level)).ToList(), bbox, filters, keywords, max, Opt.Osm.TimeoutSeconds - 5);
            var (ok, msg) = await quota.TryReserveAsync("osm", Opt.Osm.DailyCalls, Opt.Osm.MinSecondsBetweenCalls, ct);
            if (!ok) { Finish(job, CollectionJobStatus.QuotaExceeded, msg); await db.SaveChangesAsync(ct); audit.Record("collection.quota", "DataCollectionJob", job.Id, msg); await db.SaveChangesAsync(ct); return (await JobsAsync(db.CollectionJobs.Where(j => j.Id == job.Id), ct))[0]; }

            var sw = Stopwatch.StartNew();
            var json = await overpass.QueryAsync(ql, ct);
            var parsed = OverpassParser.Parse(json);
            await StoreResultsAsync(job, parsed.Candidates, area!, cat!, areas, ct);
            job.Message = parsed.Candidates.Count == 0
                ? "Aucun résultat : OpenStreetMap n'a peut-être pas de limite administrative ou d'entreprises de ce type pour cette zone. Essayez une zone plus large, une boîte géographique, ou le modèle Excel."
                : $"{parsed.Candidates.Count} résultat(s) en {sw.Elapsed.TotalSeconds:0.#} s" + (parsed.SkippedWithoutName > 0 ? $" ; {parsed.SkippedWithoutName} objet(s) sans nom ignoré(s)." : ".");
            Finish(job, CollectionJobStatus.Completed, job.Message);
        }
        catch (ConnectorException ex)
        {
            Finish(job, CollectionJobStatus.Failed, ex.Message);
        }
        catch (JsonException)
        {
            Finish(job, CollectionJobStatus.Failed, "Réponse illisible du service OpenStreetMap : réessayez plus tard.");
        }

        audit.Record("collection.run", "DataCollectionJob", job.Id, $"{job.Status}; found={job.Found}; new={job.NewCount}; dup={job.DuplicateCount}");
        await db.SaveChangesAsync(ct);
        return (await JobsAsync(db.CollectionJobs.Where(j => j.Id == job.Id), ct))[0];
    }

    private void Finish(DataCollectionJob job, CollectionJobStatus status, string? message)
    {
        job.Status = status; job.FinishedAt = clock.GetUtcNow().UtcDateTime; job.Message = message;
    }

    private async Task<List<(string Name, int Level, Guid Id)>> ResolveAreasAsync(GeographicArea a, CancellationToken ct) => a.Level switch
    {
        GeoLevel.Wilaya => [(a.Name, 4, a.Id)],
        GeoLevel.Commune => [(a.Name, 8, a.Id)],
        GeoLevel.Quartier when a.ParentId is { } pid => (await db.GeographicAreas.Where(x => x.Id == pid).Select(x => new { x.Name, x.Id }).ToListAsync(ct)).Select(x => (x.Name, 8, x.Id)).ToList(),
        _ => (await db.GeographicAreas.Where(x => x.ParentId == a.Id && x.IsActive).OrderBy(x => x.Name).Take(40).Select(x => new { x.Name, x.Id }).ToListAsync(ct)).Select(x => (x.Name, 8, x.Id)).ToList(), // a daïra is searched through its communes
    };

    private async Task StoreResultsAsync(DataCollectionJob job, IReadOnlyList<Candidate> cands, GeographicArea area, BusinessCategory cat, List<(string Name, int Level, Guid Id)> areas, CancellationToken ct)
    {
        var all = await db.GeographicAreas.AsNoTracking().ToListAsync(ct);
        GeographicArea? Up(GeographicArea? g, GeoLevel l) { while (g is not null && g.Level != l) g = g.ParentId is { } p ? all.FirstOrDefault(x => x.Id == p) : null; return g; }
        var wilaya = Up(area, GeoLevel.Wilaya)?.Id;
        var communesInScope = all.Where(c => c.Level == GeoLevel.Commune && areas.Any(x => x.Id == c.Id || x.Id == area.Id) || (c.Level == GeoLevel.Commune && Up(c, area.Level)?.Id == area.Id)).ToList();
        var extIds = cands.Select(c => c.ExternalId).ToList();
        var known = (await db.BusinessSources.Where(s => s.Provider == "osm" && s.ExternalId != null && extIds.Contains(s.ExternalId)).Select(s => new { s.ExternalId, s.BusinessId }).ToListAsync(ct)).ToDictionary(x => x.ExternalId!, x => x.BusinessId);
        var rootCat = cat.ParentId is null ? cat.Id : cat.ParentId;

        var seen = new HashSet<string>();
        foreach (var c in cands)
        {
            if (!seen.Add(c.ExternalId)) continue;
            var commune = c.City is null ? null : communesInScope.FirstOrDefault(x => x.NormalizedName == Domain.Common.TextNormalizer.NormalizeName(c.City));
            if (commune is null && area.Level == GeoLevel.Commune) commune = area;
            var daira = commune is null ? (area.Level == GeoLevel.Daira ? area : null) : Up(commune, GeoLevel.Daira);
            var payload = new ResultPayload { Candidate = c, WilayaId = wilaya, DairaId = daira?.Id, CommuneId = commune?.Id, CategoryId = rootCat, SubCategoryId = cat.ParentId is null ? null : cat.Id };
            var res = new DataCollectionJobResult { JobId = job.Id, ExternalId = c.ExternalId, Name = c.Name, CreatedAt = job.CreatedAt };
            if (known.TryGetValue(c.ExternalId, out var existing)) { res.Status = CollectionResultStatus.AlreadyImported; res.MatchBusinessId = existing; res.MatchReason = "Déjà importée depuis OpenStreetMap"; job.DuplicateCount++; }
            else
            {
                var profile = new DupProfile(Domain.Common.TextNormalizer.NormalizeName(c.Name), AlgerianPhone.Normalize(c.Phone), DuplicateDetector.WebsiteKey(c.Website), Domain.Common.TextNormalizer.Fold(c.Address), commune?.Id, c.Latitude, c.Longitude, new HashSet<string> { "osm:" + c.ExternalId });
                var match = (await duplicates.FindMatchesAsync(profile, wilaya, null, ct)).FirstOrDefault();
                if (match is not null) { res.Status = CollectionResultStatus.PotentialDuplicate; res.MatchBusinessId = match.BusinessId; res.MatchReason = $"Ressemble à « {match.Name} » ({match.Score:P0} : {string.Join(", ", match.Reasons)})"; job.DuplicateCount++; }
                else job.NewCount++;
            }

            res.PayloadJson = JsonSerializer.Serialize(payload);
            db.CollectionResults.Add(res);
            job.Found++;
        }
    }

    // ---------- Review & import ----------
    public async Task<PagedResult<JobDto>> ListJobsAsync(int page, int pageSize, CancellationToken ct = default)
    {
        RequireRun();
        (page, pageSize) = Paging.Clamp(page, pageSize);
        var q = db.CollectionJobs.AsNoTracking().AsQueryable();
        if (!user.HasPermission(Permissions.AuditView)) q = q.Where(j => j.UserId == user.Id);
        var total = await q.CountAsync(ct);
        return new PagedResult<JobDto>(await JobsAsync(q.OrderByDescending(j => j.CreatedAt).ThenBy(j => j.Id).Skip((page - 1) * pageSize).Take(pageSize), ct), total, page, pageSize);
    }

    private async Task<List<JobDto>> JobsAsync(IQueryable<DataCollectionJob> q, CancellationToken ct)
    {
        var rows = await q.AsNoTracking().Select(j => new { j.Id, j.ConnectorKey, j.UserId, j.CreatedAt, j.FinishedAt, j.Status, j.Found, j.NewCount, j.DuplicateCount, j.ImportedCount, j.SkippedCount, j.Message, j.ParametersJson, j.RetryOfJobId,
            User = db.AppUsers.Where(u => u.Id == j.UserId).Select(u => u.FullName).FirstOrDefault() }).ToListAsync(ct);
        var areaIds = rows.Select(r => { try { return JsonSerializer.Deserialize<CollectionRequest>(r.ParametersJson)?.AreaId; } catch (JsonException) { return null; } }).Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        var areaNames = await db.GeographicAreas.Where(a => areaIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        return rows.Select(r =>
        {
            var p = JsonSerializer.Deserialize<CollectionRequest>(r.ParametersJson);
            var summary = p is null ? "" : $"{(areaNames.GetValueOrDefault(p.AreaId, "?"))}{(string.IsNullOrWhiteSpace(p.Keywords) ? "" : " · " + p.Keywords)}{(string.IsNullOrWhiteSpace(p.Bbox) ? "" : " · boîte")}";
            return new JobDto(r.Id, r.ConnectorKey, r.User ?? "?", r.CreatedAt, r.FinishedAt, r.Status, r.Found, r.NewCount, r.DuplicateCount, r.ImportedCount, r.SkippedCount, r.Message, summary, r.RetryOfJobId);
        }).ToList();
    }

    private async Task<DataCollectionJob> OwnJobAsync(Guid id, CancellationToken ct)
    {
        RequireRun();
        var j = await db.CollectionJobs.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        if (j.UserId != user.Id && !user.HasPermission(Permissions.AuditView)) throw new NotFoundException();
        return j;
    }

    public async Task<(JobDto Job, PagedResult<ResultDto> Results)> GetAsync(Guid id, CollectionResultStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        var j = await OwnJobAsync(id, ct);
        (page, pageSize) = Paging.Clamp(page, pageSize);
        var q = db.CollectionResults.AsNoTracking().Where(r => r.JobId == id);
        if (status is not null) q = q.Where(r => r.Status == status);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(r => r.Name).ThenBy(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = rows.Select(r => new ResultDto(r.Id, r.ExternalId, r.Name, r.Status, JsonSerializer.Deserialize<ResultPayload>(r.PayloadJson)?.Candidate ?? new(), r.MatchReason, r.MatchBusinessId, r.BusinessId)).ToList();
        return ((await JobsAsync(db.CollectionJobs.Where(x => x.Id == j.Id), ct))[0], new PagedResult<ResultDto>(items, total, page, pageSize));
    }

    /// <summary>Creates businesses from the chosen results (nothing is imported without this explicit step). Potential duplicates only when asked.</summary>
    public async Task<(int Imported, int Skipped)> ImportAsync(Guid jobId, IReadOnlyCollection<Guid>? resultIds, bool includeDuplicates, CancellationToken ct = default)
    {
        var job = await OwnJobAsync(jobId, ct);
        if (!user.HasPermission(Permissions.BusinessCreate)) throw new ForbiddenException();
        var q = db.CollectionResults.Where(r => r.JobId == jobId && (r.Status == CollectionResultStatus.New || (includeDuplicates && r.Status == CollectionResultStatus.PotentialDuplicate)));
        if (resultIds is { Count: > 0 }) q = q.Where(r => resultIds.Contains(r.Id));
        var rows = await q.OrderBy(r => r.Name).Take(500).ToListAsync(ct);
        if (rows.Count == 0) throw new ValidationException("Aucun résultat importable dans la sélection.");

        await using var tx = await UnitOfWork.BeginAsync(db.Database, ct);
        var created = new List<(DataCollectionJobResult Row, Domain.Businesses.Business Biz)>(); var skipped = 0;
        foreach (var row in rows)
        {
            var p = JsonSerializer.Deserialize<ResultPayload>(row.PayloadJson)!;
            var c = p.Candidate;
            var input = new BusinessInput { Name = c.Name, Phone = c.Phone, Website = c.Website, Address = c.Address, Latitude = c.Latitude, Longitude = c.Longitude, SourceUrl = c.SourceUrl, Description = c.Description,
                WilayaId = p.WilayaId, DairaId = p.DairaId, CommuneId = p.CommuneId, CategoryId = p.CategoryId, SubCategoryId = p.SubCategoryId };
            if ((await businesses.ValidateAsync(input, ct)).Count > 0) { row.Status = CollectionResultStatus.Rejected; row.MatchReason = "Données invalides : fiche non créée."; skipped++; continue; }
            var biz = await businesses.BuildNewAsync(input, FieldOrigin.External, new BusinessService.SourceInfo(SourceType.OpenStreetMap, "osm", c.ExternalId, c.SourceUrl), ct);
            created.Add((row, biz));
        }

        await db.SaveChangesAsync(ct);
        foreach (var (row, biz) in created) { row.Status = CollectionResultStatus.Imported; row.BusinessId = biz.Id; }
        job.ImportedCount += created.Count; job.SkippedCount += skipped;
        audit.Record("collection.import", "DataCollectionJob", job.Id, $"imported={created.Count}; skipped={skipped}");
        await db.SaveChangesAsync(ct);
        foreach (var (_, biz) in created) await duplicates.DetectAsync(biz, ct);
        await tx.CommitAsync(ct);
        return (created.Count, skipped);
    }

    public async Task RejectAsync(Guid jobId, IReadOnlyCollection<Guid> resultIds, CancellationToken ct = default)
    {
        var job = await OwnJobAsync(jobId, ct);
        var rows = await db.CollectionResults.Where(r => r.JobId == jobId && resultIds.Contains(r.Id) && (r.Status == CollectionResultStatus.New || r.Status == CollectionResultStatus.PotentialDuplicate)).ToListAsync(ct);
        foreach (var r in rows) r.Status = CollectionResultStatus.Rejected;
        job.SkippedCount += rows.Count;
        audit.Record("collection.reject", "DataCollectionJob", jobId, $"count={rows.Count}");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Re-runs a failed or incomplete search with the same parameters; results already imported are recognised, not duplicated.</summary>
    public async Task<JobDto> RetryAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await OwnJobAsync(jobId, ct);
        var p = JsonSerializer.Deserialize<CollectionRequest>(job.ParametersJson) ?? throw new ValidationException("Paramètres illisibles.");
        return await SearchAsync(p, job.Id, ct);
    }
}
