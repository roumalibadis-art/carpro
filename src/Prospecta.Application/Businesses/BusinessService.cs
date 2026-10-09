using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Common;
using Prospecta.Application.Duplicates;
using Prospecta.Application.Security;
using Prospecta.Domain.Businesses;
using Prospecta.Domain.Common;
using Prospecta.Domain.Geography;

namespace Prospecta.Application.Businesses;

public sealed class BusinessService(IAppDbContext db, ICurrentUser user, IAuditService audit, TimeProvider clock, DuplicateService duplicates)
{
    // ---------- Access scope (server-side, never trusts the UI) ----------

    private void RequireAuth()
    {
        if (!user.IsAuthenticated || user.Id is null) throw new ForbiddenException();
    }

    private bool SeesAll => user.HasPermission(Permissions.BusinessViewAll);

    /// <summary>
    /// The only gate for reading businesses: organization-wide for ViewAll holders, otherwise only the businesses
    /// actively assigned to the caller. Out-of-scope ids behave exactly like missing ones (no existence leak).
    /// </summary>
    public IQueryable<Business> Scoped()
    {
        RequireAuth();
        if (!user.HasPermission(Permissions.BusinessView) && !SeesAll) throw new ForbiddenException();
        var q = db.Businesses.AsQueryable();
        if (SeesAll) return q;
        var uid = user.Id!.Value;
        return q.Where(b => b.Assignments.Any(a => a.UserId == uid && a.IsActive));
    }

    public IQueryable<Business> Filtered(BusinessFilter f)
    {
        var q = Scoped();
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var term = TextNormalizer.Fold(f.Search);
            var digits = new string(f.Search.Where(char.IsDigit).ToArray());
            var phoneKey = AlgerianPhone.Normalize(f.Search) ?? (digits.Length >= 4 ? digits.TrimStart('0') : null);
            q = q.Where(b => b.NormalizedName.Contains(term) || (b.Address != null && b.Address.Contains(f.Search.Trim())) ||
                             (phoneKey != null && phoneKey.Length >= 3 && b.NormalizedPhone != null && b.NormalizedPhone.Contains(phoneKey)) ||
                             (b.WebsiteHost != null && b.WebsiteHost.Contains(f.Search.Trim().ToLower())));
        }

        if (f.WilayaId is not null) q = q.Where(b => b.WilayaId == f.WilayaId);
        if (f.DairaId is not null) q = q.Where(b => b.DairaId == f.DairaId);
        if (f.CommuneId is not null) q = q.Where(b => b.CommuneId == f.CommuneId);
        if (f.DistrictId is not null) q = q.Where(b => b.DistrictId == f.DistrictId);
        if (f.CategoryId is not null) q = q.Where(b => b.CategoryId == f.CategoryId);
        if (f.SubCategoryId is not null) q = q.Where(b => b.SubCategoryId == f.SubCategoryId);
        if (f.SourceType is not null) q = q.Where(b => b.Sources.Any(s => s.SourceType == f.SourceType));
        if (f.ResponsibleUserId is not null) q = q.Where(b => b.Assignments.Any(a => a.IsActive && a.UserId == f.ResponsibleUserId));
        if (f.CollectedFrom is not null) q = q.Where(b => b.CollectedAt >= f.CollectedFrom);
        if (f.CollectedTo is not null) q = q.Where(b => b.CollectedAt < f.CollectedTo.Value.Date.AddDays(1));
        if (f.VerifiedFrom is not null) q = q.Where(b => b.LastVerifiedAt >= f.VerifiedFrom);
        if (f.VerifiedTo is not null) q = q.Where(b => b.LastVerifiedAt < f.VerifiedTo.Value.Date.AddDays(1));
        if (f.StaleBefore is not null) q = q.Where(b => b.LastVerifiedAt == null || b.LastVerifiedAt < f.StaleBefore);
        if (f.MinCompleteness is not null) q = q.Where(b => b.CompletenessPercent >= f.MinCompleteness);
        if (f.MaxCompleteness is not null) q = q.Where(b => b.CompletenessPercent <= f.MaxCompleteness);
        if (f.CensusStatusId is not null) q = q.Where(b => b.CensusStatusId == f.CensusStatusId);
        if (f.ProcessingStatusId is not null) q = q.Where(b => b.ProcessingStatusId == f.ProcessingStatusId);
        if (f.OutcomeStatusId is not null) q = q.Where(b => b.OutcomeStatusId == f.OutcomeStatusId);
        if (f.Priority is not null) q = q.Where(b => b.Priority == f.Priority);
        if (f.HasPhone is { } hp) q = hp ? q.Where(b => b.NormalizedPhone != null) : q.Where(b => b.NormalizedPhone == null);
        if (f.HasWebsite is { } hw) q = hw ? q.Where(b => b.Website != null) : q.Where(b => b.Website == null);
        if (f.HasCoordinates is { } hc) q = hc ? q.Where(b => b.Latitude != null && b.Longitude != null) : q.Where(b => b.Latitude == null || b.Longitude == null);
        if (f.HasContactError is { } ce) q = q.Where(b => b.HasContactError == ce);
        if (f.ChangeReported is { } cr) q = q.Where(b => b.ChangeReported == cr);
        if (f.PendingDuplicate is { } pd)
        {
            q = pd
                ? q.Where(b => db.DuplicateCandidates.Any(c => c.Status == DuplicateStatus.Pending && (c.BusinessAId == b.Id || c.BusinessBId == b.Id)))
                : q.Where(b => !db.DuplicateCandidates.Any(c => c.Status == DuplicateStatus.Pending && (c.BusinessAId == b.Id || c.BusinessBId == b.Id)));
        }

        return q;
    }

    private static IQueryable<Business> Sort(IQueryable<Business> q, string? sortBy, bool desc) => (sortBy?.ToLowerInvariant(), desc) switch
    {
        ("collected", false) => q.OrderBy(b => b.CollectedAt).ThenBy(b => b.Id),
        ("collected", true) => q.OrderByDescending(b => b.CollectedAt).ThenBy(b => b.Id),
        ("verified", false) => q.OrderBy(b => b.LastVerifiedAt).ThenBy(b => b.Id),
        ("verified", true) => q.OrderByDescending(b => b.LastVerifiedAt).ThenBy(b => b.Id),
        ("completeness", false) => q.OrderBy(b => b.CompletenessPercent).ThenBy(b => b.Id),
        ("completeness", true) => q.OrderByDescending(b => b.CompletenessPercent).ThenBy(b => b.Id),
        ("commune", false) => q.OrderBy(b => b.Commune!.Name).ThenBy(b => b.Id),
        ("commune", true) => q.OrderByDescending(b => b.Commune!.Name).ThenBy(b => b.Id),
        (_, true) => q.OrderByDescending(b => b.NormalizedName).ThenBy(b => b.Id),
        _ => q.OrderBy(b => b.NormalizedName).ThenBy(b => b.Id),
    };

    public async Task<PagedResult<BusinessListItem>> SearchAsync(BusinessFilter f, CancellationToken ct = default)
    {
        var (page, size) = Paging.Clamp(f.Page, f.PageSize);
        var q = Filtered(f);
        var total = await q.CountAsync(ct);
        var rows = await Sort(q, f.SortBy, f.SortDesc).Skip((page - 1) * size).Take(size).AsNoTracking()
            .Select(b => new
            {
                b.Id, b.Name, Category = b.Category!.Name, SubCategory = b.SubCategory!.Name, Wilaya = b.Wilaya!.Name, Daira = b.Daira!.Name,
                Commune = b.Commune!.Name, b.Phone, b.Website,
                Census = new StatusRef(b.CensusStatusId, b.CensusStatus!.Code, b.CensusStatus.Label),
                Processing = new StatusRef(b.ProcessingStatusId, b.ProcessingStatus!.Code, b.ProcessingStatus.Label),
                Outcome = new StatusRef(b.OutcomeStatusId, b.OutcomeStatus!.Code, b.OutcomeStatus.Label),
                b.Priority, b.CompletenessPercent, b.Confidence, b.CollectedAt, b.LastVerifiedAt, b.Latitude, b.Longitude,
                Dup = db.DuplicateCandidates.Any(c => c.Status == DuplicateStatus.Pending && (c.BusinessAId == b.Id || c.BusinessBId == b.Id)),
            }).ToListAsync(ct);
        var pageIds = rows.Select(r => r.Id).ToList();
        var resp = await ResponsibleAsync(db.BusinessAssignments.Where(a => pageIds.Contains(a.BusinessId)), ct);
        var items = rows.Select(r => new BusinessListItem(r.Id, r.Name, r.Category, r.SubCategory, r.Wilaya, r.Daira, r.Commune, r.Phone, r.Website,
            r.Census, r.Processing, r.Outcome, r.Priority, r.CompletenessPercent, r.Confidence, r.CollectedAt, r.LastVerifiedAt, r.Latitude, r.Longitude,
            resp.GetValueOrDefault(r.Id) ?? [], r.Dup)).ToList();
        return new PagedResult<BusinessListItem>(items, total, page, size);
    }

    /// <summary>Active responsible names per business (two simple queries: portable across SQLite and MySQL, no APPLY).</summary>
    public async Task<Dictionary<Guid, List<string>>> ResponsibleAsync(IQueryable<BusinessAssignment> assignments, CancellationToken ct)
    {
        var rows = await (from a in assignments.Where(a => a.IsActive)
                          join u in db.AppUsers on a.UserId equals u.Id
                          select new { a.BusinessId, u.FullName }).ToListAsync(ct);
        return rows.GroupBy(r => r.BusinessId).ToDictionary(g => g.Key, g => g.Select(x => x.FullName).OrderBy(x => x).ToList());
    }

    // ---------- Read ----------

    public async Task<BusinessDetail> GetAsync(Guid id, CancellationToken ct = default)
    {
        var b = await Scoped().AsNoTracking().Include(x => x.Category).Include(x => x.SubCategory).Include(x => x.Wilaya).Include(x => x.Daira)
            .Include(x => x.Commune).Include(x => x.District).Include(x => x.CensusStatus).Include(x => x.ProcessingStatus).Include(x => x.OutcomeStatus)
            .Include(x => x.Provenances).Include(x => x.Sources).Include(x => x.Assignments)
            .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        var userIds = b.Assignments.Where(a => a.IsActive).Select(a => a.UserId).Append(b.LastVerifiedByUserId ?? Guid.Empty).ToList();
        var names = await db.AppUsers.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        return ToDetail(b, names);
    }

    private BusinessDetail ToDetail(Business b, Dictionary<Guid, string> names) => new(
        b.Id, ToInput(b), b.Category?.Name, b.SubCategory?.Name, b.Wilaya?.Name, b.Daira?.Name, b.Commune?.Name, b.District?.Name,
        Ref(b.CensusStatus!), Ref(b.ProcessingStatus!), Ref(b.OutcomeStatus!), b.CompletenessPercent, b.Confidence, b.CollectedAt, b.LastVerifiedAt,
        b.LastVerifiedByUserId is { } v && names.TryGetValue(v, out var vn) ? vn : null, b.HasContactError, b.ChangeReported, b.IsDemo,
        b.CreatedAt, b.UpdatedAt, b.Provenances.ToDictionary(p => p.Field, p => p.Origin),
        b.Sources.OrderBy(s => s.CollectedAt).Select(s => new SourceDto(s.SourceType, s.Provider, s.ExternalId, s.Url, s.CollectedAt)).ToList(),
        b.Assignments.Where(a => a.IsActive).Select(a => new AssignmentDto(a.UserId, names.GetValueOrDefault(a.UserId, "?"), a.CreatedAt)).ToList(),
        user.HasPermission(Permissions.BusinessEdit), user.HasPermission(Permissions.BusinessVerify));

    private static StatusRef Ref(StatusValue s) => new(s.Id, s.Code, s.Label);

    private static BusinessInput ToInput(Business b) => new()
    {
        Name = b.Name, LegalName = b.LegalName, Description = b.Description, CategoryId = b.CategoryId, SubCategoryId = b.SubCategoryId,
        WilayaId = b.WilayaId, DairaId = b.DairaId, CommuneId = b.CommuneId, DistrictId = b.DistrictId, Address = b.Address,
        Latitude = b.Latitude, Longitude = b.Longitude, PlusCode = b.PlusCode, Phone = b.Phone, Website = b.Website,
        GoogleMapsUrl = b.GoogleMapsUrl, SourceUrl = b.SourceUrl, InternalNotes = b.InternalNotes, Priority = b.Priority,
    };

    public async Task<PagedResult<HistoryDto>> HistoryAsync(Guid id, int page, int pageSize, CancellationToken ct = default)
    {
        if (!await Scoped().AnyAsync(b => b.Id == id, ct)) throw new NotFoundException();
        (page, pageSize) = Paging.Clamp(page, pageSize);
        var q = db.BusinessHistory.AsNoTracking().Where(h => h.BusinessId == id);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(h => h.CreatedAt).ThenByDescending(h => h.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(h => new HistoryDto(h.Field, h.OldValue, h.NewValue, h.Origin,
                db.AppUsers.Where(u => u.Id == h.ChangedByUserId).Select(u => u.FullName).FirstOrDefault(), h.Reason, h.CreatedAt))
            .ToListAsync(ct);
        return new PagedResult<HistoryDto>(rows, total, page, pageSize);
    }

    // ---------- Validation ----------

    /// <summary>Validates the input and fills the geographic ancestors from the deepest chosen area.</summary>
    public async Task<List<string>> ValidateAsync(BusinessInput i, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(i.Name)) errors.Add("Le nom commercial est obligatoire.");
        else if (i.Name.Trim().Length > 200) errors.Add("Le nom ne peut dépasser 200 caractères.");
        if (i.LegalName?.Length > 200) errors.Add("Le nom légal ne peut dépasser 200 caractères.");
        if (i.Description?.Length > 2000) errors.Add("La description ne peut dépasser 2000 caractères.");
        if (i.Address?.Length > 300) errors.Add("L'adresse ne peut dépasser 300 caractères.");
        if (i.InternalNotes?.Length > 4000) errors.Add("Les commentaires internes ne peuvent dépasser 4000 caractères.");
        if (!string.IsNullOrWhiteSpace(i.Phone) && !AlgerianPhone.IsValid(i.Phone)) errors.Add($"Numéro de téléphone algérien invalide : « {i.Phone} ».");
        if (!string.IsNullOrWhiteSpace(i.Website) && TextNormalizer.WebsiteHost(i.Website) is null) errors.Add("Site web invalide (URL http/https attendue).");
        foreach (var (label, url) in new[] { ("URL Google Maps", i.GoogleMapsUrl), ("URL source", i.SourceUrl) })
        {
            if (!string.IsNullOrWhiteSpace(url) && TextNormalizer.WebsiteHost(url) is null) errors.Add($"{label} invalide.");
        }

        if (i.Latitude is not null != (i.Longitude is not null)) errors.Add("La latitude et la longitude doivent être renseignées ensemble.");
        else if (i.Latitude is { } la && i.Longitude is { } lo && !BusinessRules.CoordinatesPlausible(la, lo)) errors.Add("Coordonnées hors d'Algérie : vérifiez latitude et longitude.");

        errors.AddRange(await ResolveGeographyAsync(i, ct));
        errors.AddRange(await ValidateCategoriesAsync(i, ct));
        return errors;
    }

    private async Task<List<string>> ResolveGeographyAsync(BusinessInput i, CancellationToken ct)
    {
        var errors = new List<string>();
        var ids = new[] { i.WilayaId, i.DairaId, i.CommuneId, i.DistrictId }.Where(x => x is not null).Select(x => x!.Value).ToList();
        if (ids.Count == 0) return errors;
        var areas = await db.GeographicAreas.AsNoTracking().ToDictionaryAsync(a => a.Id, ct); // small referential (≈ 2k rows)
        GeographicArea? Get(Guid? id) => id is { } g && areas.TryGetValue(g, out var a) ? a : null;

        foreach (var (id, level, label) in new[] { (i.WilayaId, GeoLevel.Wilaya, "wilaya"), (i.DairaId, GeoLevel.Daira, "daïra"), (i.CommuneId, GeoLevel.Commune, "commune"), (i.DistrictId, GeoLevel.Quartier, "quartier") })
        {
            if (id is null) continue;
            var a = Get(id);
            if (a is null || a.Level != level) errors.Add($"Zone « {label} » inconnue.");
        }

        if (errors.Count > 0) return errors;

        // Deepest chosen area wins; its ancestors are derived, and any explicit ancestor must agree.
        var deepest = Get(i.DistrictId) ?? Get(i.CommuneId) ?? Get(i.DairaId) ?? Get(i.WilayaId)!;
        var chain = new Dictionary<GeoLevel, Guid>();
        for (var cur = deepest; cur is not null; cur = Get(cur.ParentId)) chain[cur.Level] = cur.Id;
        foreach (var (level, supplied, label) in new[] { (GeoLevel.Wilaya, i.WilayaId, "wilaya"), (GeoLevel.Daira, i.DairaId, "daïra"), (GeoLevel.Commune, i.CommuneId, "commune") })
        {
            if (supplied is not null && chain.TryGetValue(level, out var derived) && derived != supplied)
                errors.Add($"La {label} choisie ne correspond pas à la hiérarchie sélectionnée.");
        }

        if (errors.Count > 0) return errors;
        if (Get(deepest.Id)!.IsActive == false) errors.Add($"La zone « {deepest.Name} » est désactivée.");
        i.WilayaId = chain.GetValueOrDefault(GeoLevel.Wilaya) is var w && w != default ? w : null;
        i.DairaId = chain.GetValueOrDefault(GeoLevel.Daira) is var d && d != default ? d : null;
        i.CommuneId = chain.GetValueOrDefault(GeoLevel.Commune) is var c && c != default ? c : null;
        i.DistrictId = chain.GetValueOrDefault(GeoLevel.Quartier) is var q && q != default ? q : null;
        return errors;
    }

    private async Task<List<string>> ValidateCategoriesAsync(BusinessInput i, CancellationToken ct)
    {
        var errors = new List<string>();
        if (i.SubCategoryId is not null && i.CategoryId is null)
        {
            var sub0 = await db.BusinessCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == i.SubCategoryId, ct);
            if (sub0?.ParentId is { } p) i.CategoryId = p;
        }

        if (i.CategoryId is not null)
        {
            var cat = await db.BusinessCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == i.CategoryId, ct);
            if (cat is null || cat.ParentId is not null) errors.Add("Activité principale inconnue.");
        }

        if (i.SubCategoryId is not null)
        {
            var sub = await db.BusinessCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == i.SubCategoryId, ct);
            if (sub is null || sub.ParentId != i.CategoryId) errors.Add("La sous-activité n'appartient pas à l'activité choisie.");
        }

        return errors;
    }

    // ---------- Write ----------

    private async Task<Guid> StatusIdAsync(StatusKind kind, string code, CancellationToken ct) =>
        (await db.StatusValues.AsNoTracking().FirstAsync(s => s.Kind == kind && s.Code == code, ct)).Id;

    private static void ApplyAll(FieldUpdater u, BusinessInput i, FieldOrigin origin, string? reason = null)
    {
        u.Apply("Name", i.Name?.Trim(), origin, reason);
        u.Apply("LegalName", i.LegalName, origin, reason);
        u.Apply("Description", i.Description, origin, reason);
        u.Apply("Address", i.Address, origin, reason);
        u.Apply("Phone", AlgerianPhone.Normalize(i.Phone) is { } n ? AlgerianPhone.Format(n) : null, origin, reason);
        u.Apply("Website", i.Website, origin, reason);
        u.Apply("GoogleMapsUrl", i.GoogleMapsUrl, origin, reason);
        u.Apply("SourceUrl", i.SourceUrl, origin, reason);
        u.Apply("PlusCode", i.PlusCode, origin, reason);
        u.Apply("Latitude", BusinessRules.FormatDouble(i.Latitude), origin, reason);
        u.Apply("Longitude", BusinessRules.FormatDouble(i.Longitude), origin, reason);
        u.Apply("CategoryId", i.CategoryId?.ToString(), origin, reason);
        u.Apply("SubCategoryId", i.SubCategoryId?.ToString(), origin, reason);
        u.Apply("WilayaId", i.WilayaId?.ToString(), origin, reason);
        u.Apply("DairaId", i.DairaId?.ToString(), origin, reason);
        u.Apply("CommuneId", i.CommuneId?.ToString(), origin, reason);
        u.Apply("DistrictId", i.DistrictId?.ToString(), origin, reason);
    }

    public sealed record SourceInfo(SourceType Type, string Provider, string? ExternalId = null, string? Url = null, Guid? ImportBatchId = null, bool IsDemo = false);

    /// <summary>Creates a business without saving (shared by manual entry and import). Input must already be validated.</summary>
    public async Task<Business> BuildNewAsync(BusinessInput input, FieldOrigin origin, SourceInfo source, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var b = new Business
        {
            CreatedAt = now, UpdatedAt = now, CollectedAt = now, CreatedByUserId = user.Id, UpdatedByUserId = user.Id,
            Priority = input.Priority, InternalNotes = input.InternalNotes, IsDemo = source.IsDemo,
            ProcessingStatusId = await StatusIdAsync(StatusKind.Processing, StatusCodes.Unassigned, ct),
            OutcomeStatusId = await StatusIdAsync(StatusKind.Outcome, StatusCodes.Pending, ct),
        };
        var src = new BusinessSource
        {
            BusinessId = b.Id, SourceType = source.Type, Provider = source.Provider, ExternalId = source.ExternalId, Url = source.Url,
            ImportBatchId = source.ImportBatchId, CollectedAt = now, CreatedAt = now,
        };
        b.Sources.Add(src);
        var updater = new FieldUpdater(b, clock, user.Id, false, src.Id);
        ApplyAll(updater, input, origin, "Création");
        BusinessRules.Finalize(b);
        b.CensusStatusId = await StatusIdAsync(StatusKind.Census, b.CompletenessPercent < 50 ? StatusCodes.Partial : StatusCodes.ToVerify, ct);
        updater.History.Add(new BusinessDataHistory { BusinessId = b.Id, Field = "Created", NewValue = b.Name, Origin = origin, ChangedByUserId = user.Id, CreatedAt = now, Reason = source.Provider });
        db.Businesses.Add(b);
        db.BusinessHistory.AddRange(updater.History);
        return b;
    }

    public async Task<SaveResult> CreateAsync(BusinessInput input, CancellationToken ct = default)
    {
        RequireAuth();
        if (!user.HasPermission(Permissions.BusinessCreate)) throw new ForbiddenException();
        var errors = await ValidateAsync(input, ct);
        if (errors.Count > 0) throw new ValidationException(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var b = await BuildNewAsync(input, FieldOrigin.Manual, new SourceInfo(SourceType.Manual, "manual"), ct);
        // The creator of a record stays able to work on it even without organization-wide visibility.
        if (!SeesAll)
        {
            b.Assignments.Add(new BusinessAssignment { BusinessId = b.Id, UserId = user.Id!.Value, AssignedByUserId = user.Id, CreatedAt = b.CreatedAt });
            b.ProcessingStatusId = await StatusIdAsync(StatusKind.Processing, StatusCodes.Assigned, ct);
        }

        audit.Record("business.create", "Business", b.Id, b.Name);
        await db.SaveChangesAsync(ct);
        var warnings = (await duplicates.DetectAsync(b, ct)).Select(m => $"Doublon possible : {m.Name} ({m.Score:P0})").ToList();
        await tx.CommitAsync(ct);
        return new SaveResult(await GetAsync(b.Id, ct), [], warnings);
    }

    public async Task<SaveResult> UpdateAsync(Guid id, BusinessInput input, CancellationToken ct = default)
    {
        if (!user.HasPermission(Permissions.BusinessEdit)) throw new ForbiddenException();
        var errors = await ValidateAsync(input, ct);
        if (errors.Count > 0) throw new ValidationException(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var b = await Scoped().Include(x => x.Provenances).Include(x => x.Sources).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        var identityBefore = (b.NormalizedName, b.NormalizedPhone, b.WebsiteHost, b.Address, b.Latitude, b.Longitude, b.CommuneId);
        var updater = new FieldUpdater(b, clock, user.Id, user.HasPermission(Permissions.BusinessVerify));
        ApplyAll(updater, input, FieldOrigin.Manual);

        if (b.InternalNotes != input.InternalNotes)
        {
            updater.History.Add(new BusinessDataHistory { BusinessId = b.Id, Field = "InternalNotes", OldValue = Trunc(b.InternalNotes), NewValue = Trunc(input.InternalNotes), Origin = FieldOrigin.Manual, ChangedByUserId = user.Id, CreatedAt = clock.GetUtcNow().UtcDateTime });
            b.InternalNotes = input.InternalNotes;
        }

        if (b.Priority != input.Priority)
        {
            updater.History.Add(new BusinessDataHistory { BusinessId = b.Id, Field = "Priority", OldValue = b.Priority.ToString(), NewValue = input.Priority.ToString(), Origin = FieldOrigin.Manual, ChangedByUserId = user.Id, CreatedAt = clock.GetUtcNow().UtcDateTime });
            b.Priority = input.Priority;
        }

        b.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        b.UpdatedByUserId = user.Id;
        BusinessRules.Finalize(b);
        db.BusinessHistory.AddRange(updater.History);
        audit.Record("business.update", "Business", b.Id, string.Join(",", updater.History.Select(h => h.Field)));
        await db.SaveChangesAsync(ct);

        var warnings = new List<string>();
        var identityAfter = (b.NormalizedName, b.NormalizedPhone, b.WebsiteHost, b.Address, b.Latitude, b.Longitude, b.CommuneId);
        if (identityAfter != identityBefore)
        {
            warnings.AddRange((await duplicates.DetectAsync(b, ct)).Select(m => $"Doublon possible : {m.Name} ({m.Score:P0})"));
        }

        await tx.CommitAsync(ct);
        return new SaveResult(await GetAsync(id, ct), updater.BlockedFields.Select(f => $"« {f} » est confirmé : seule une personne habilitée à vérifier peut le modifier.").ToList(), warnings);
    }

    private static string? Trunc(string? s) => s is { Length: > 200 } ? s[..200] + "…" : s;

    public async Task DeleteAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        if (!user.HasPermission(Permissions.BusinessDelete)) throw new ForbiddenException();
        var b = await Scoped().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        b.IsDeleted = true; b.DeletedAt = clock.GetUtcNow().UtcDateTime; b.UpdatedAt = b.DeletedAt.Value; b.UpdatedByUserId = user.Id;
        db.BusinessHistory.Add(new BusinessDataHistory { BusinessId = b.Id, Field = "Deleted", OldValue = b.Name, Origin = FieldOrigin.Manual, ChangedByUserId = user.Id, CreatedAt = b.DeletedAt.Value, Reason = reason });
        audit.Record("business.delete", "Business", b.Id, reason);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Human verification: stamps the date and marks the currently known values as confirmed.</summary>
    public async Task VerifyAsync(Guid id, CancellationToken ct = default)
    {
        if (!user.HasPermission(Permissions.BusinessVerify)) throw new ForbiddenException();
        var b = await Scoped().Include(x => x.Provenances).Include(x => x.Sources).Include(x => x.CensusStatus).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        var now = clock.GetUtcNow().UtcDateTime;
        var previous = b.CensusStatus?.Label;
        new FieldUpdater(b, clock, user.Id, true).ConfirmAll();
        b.LastVerifiedAt = now; b.LastVerifiedByUserId = user.Id; b.HasContactError = false; b.ChangeReported = false;
        b.CensusStatusId = await StatusIdAsync(StatusKind.Census, StatusCodes.Verified, ct);
        b.UpdatedAt = now; b.UpdatedByUserId = user.Id;
        BusinessRules.Finalize(b);
        db.BusinessHistory.Add(new BusinessDataHistory { BusinessId = b.Id, Field = "Verified", OldValue = previous, NewValue = "Vérifié", Origin = FieldOrigin.Confirmed, ChangedByUserId = user.Id, CreatedAt = now });
        audit.Record("business.verify", "Business", b.Id);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetStatusAsync(Guid id, StatusKind kind, Guid statusId, CancellationToken ct = default)
    {
        if (!user.HasPermission(Permissions.BusinessEdit)) throw new ForbiddenException();
        var status = await db.StatusValues.FirstOrDefaultAsync(s => s.Id == statusId && s.Kind == kind && s.IsActive, ct) ?? throw new ValidationException("Statut inconnu pour cette catégorie.");
        var b = await Scoped().Include(x => x.CensusStatus).Include(x => x.ProcessingStatus).Include(x => x.OutcomeStatus).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        if (kind == StatusKind.Census && status.Code == StatusCodes.Verified && !user.HasPermission(Permissions.BusinessVerify))
            throw new ForbiddenException("Seule une personne habilitée à vérifier peut marquer une fiche « Vérifié ».");
        ApplyStatus(b, kind, status);
        await db.SaveChangesAsync(ct);
    }

    private void ApplyStatus(Business b, StatusKind kind, StatusValue status)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        string? old;
        switch (kind)
        {
            case StatusKind.Census: old = b.CensusStatus?.Label; b.CensusStatusId = status.Id; break;
            case StatusKind.Processing: old = b.ProcessingStatus?.Label; b.ProcessingStatusId = status.Id; break;
            default: old = b.OutcomeStatus?.Label; b.OutcomeStatusId = status.Id; break;
        }

        b.UpdatedAt = now; b.UpdatedByUserId = user.Id;
        db.BusinessHistory.Add(new BusinessDataHistory { BusinessId = b.Id, Field = $"{kind}Status", OldValue = old, NewValue = status.Label, Origin = FieldOrigin.Manual, ChangedByUserId = user.Id, CreatedAt = now });
        audit.Record("business.status", "Business", b.Id, $"{kind}={status.Code}");
    }

    public async Task SetFlagsAsync(Guid id, bool? contactError, bool? changeReported, CancellationToken ct = default)
    {
        if (!user.HasPermission(Permissions.BusinessEdit)) throw new ForbiddenException();
        var b = await Scoped().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        if (contactError is { } ce) b.HasContactError = ce;
        if (changeReported is { } cr) b.ChangeReported = cr;
        b.UpdatedAt = clock.GetUtcNow().UtcDateTime; b.UpdatedByUserId = user.Id;
        audit.Record("business.flags", "Business", b.Id, $"contactError={b.HasContactError};changeReported={b.ChangeReported}");
        await db.SaveChangesAsync(ct);
    }

    // ---------- Assignment & bulk actions ----------

    /// <summary>Assigns businesses to a user. Census status is untouched; processing moves "unassigned" → "assigned" only.</summary>
    public async Task<int> AssignAsync(IReadOnlyCollection<Guid> businessIds, Guid assigneeId, CancellationToken ct = default)
    {
        if (!user.HasPermission(Permissions.BusinessAssign)) throw new ForbiddenException();
        if (businessIds.Count is 0 or > 500) throw new ValidationException("Sélectionnez entre 1 et 500 entreprises.");
        var assignee = await db.AppUsers.FirstOrDefaultAsync(u => u.Id == assigneeId && u.IsActive, ct) ?? throw new ValidationException("Utilisateur introuvable ou désactivé.");
        var unassigned = await StatusIdAsync(StatusKind.Processing, StatusCodes.Unassigned, ct);
        var assigned = await StatusIdAsync(StatusKind.Processing, StatusCodes.Assigned, ct);
        var now = clock.GetUtcNow().UtcDateTime;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var businesses = await Scoped().Include(b => b.Assignments).Where(b => businessIds.Contains(b.Id)).ToListAsync(ct);
        var done = 0;
        foreach (var b in businesses)
        {
            if (b.Assignments.Any(a => a.IsActive && a.UserId == assigneeId)) continue;
            b.Assignments.Add(new BusinessAssignment { BusinessId = b.Id, UserId = assigneeId, AssignedByUserId = user.Id, CreatedAt = now });
            if (b.ProcessingStatusId == unassigned) b.ProcessingStatusId = assigned;
            b.UpdatedAt = now; b.UpdatedByUserId = user.Id;
            db.BusinessHistory.Add(new BusinessDataHistory { BusinessId = b.Id, Field = "Assignment", NewValue = assignee.FullName, Origin = FieldOrigin.Manual, ChangedByUserId = user.Id, CreatedAt = now });
            done++;
        }

        audit.Record("business.assign", "Business", null, $"user={assigneeId}; count={done}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return done;
    }

    public async Task UnassignAsync(Guid businessId, Guid userId, CancellationToken ct = default)
    {
        if (!user.HasPermission(Permissions.BusinessAssign)) throw new ForbiddenException();
        var b = await Scoped().Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == businessId, ct) ?? throw new NotFoundException();
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var a in b.Assignments.Where(a => a.IsActive && a.UserId == userId)) { a.IsActive = false; a.EndedAt = now; }
        if (!b.Assignments.Any(a => a.IsActive) && await db.StatusValues.AnyAsync(s => s.Id == b.ProcessingStatusId && s.Code == StatusCodes.Assigned, ct))
        {
            b.ProcessingStatusId = await StatusIdAsync(StatusKind.Processing, StatusCodes.Unassigned, ct);
        }

        db.BusinessHistory.Add(new BusinessDataHistory { BusinessId = b.Id, Field = "Unassignment", OldValue = userId.ToString(), Origin = FieldOrigin.Manual, ChangedByUserId = user.Id, CreatedAt = now });
        audit.Record("business.unassign", "Business", b.Id, $"user={userId}");
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> BulkSetStatusAsync(IReadOnlyCollection<Guid> ids, StatusKind kind, Guid statusId, CancellationToken ct = default)
    {
        if (!user.HasPermission(Permissions.BusinessEdit)) throw new ForbiddenException();
        if (ids.Count is 0 or > 500) throw new ValidationException("Sélectionnez entre 1 et 500 entreprises.");
        var status = await db.StatusValues.FirstOrDefaultAsync(s => s.Id == statusId && s.Kind == kind && s.IsActive, ct) ?? throw new ValidationException("Statut inconnu pour cette catégorie.");
        if (kind == StatusKind.Census && status.Code == StatusCodes.Verified && !user.HasPermission(Permissions.BusinessVerify))
            throw new ForbiddenException("Seule une personne habilitée à vérifier peut marquer des fiches « Vérifié ».");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var list = await Scoped().Include(x => x.CensusStatus).Include(x => x.ProcessingStatus).Include(x => x.OutcomeStatus).Where(b => ids.Contains(b.Id)).ToListAsync(ct);
        foreach (var b in list) ApplyStatus(b, kind, status);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return list.Count;
    }

    public static string FilterToJson(BusinessFilter f) => JsonSerializer.Serialize(f);
}
