using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Common;
using Prospecta.Application.Security;
using Prospecta.Domain.Common;
using Prospecta.Domain.Geography;

namespace Prospecta.Application.Reference;

public sealed record GeoDto(Guid Id, GeoLevel Level, string Code, string Name, Guid? ParentId, double? Latitude, double? Longitude, bool IsActive);
public sealed record CategoryDto(Guid Id, string Name, Guid? ParentId, bool IsActive, string? OsmFilter = null);
public sealed record StatusDto(Guid Id, StatusKind Kind, string Code, string Label, int SortOrder, bool IsActive, bool IsSystem);

public sealed class GeoSave
{
    public GeoLevel Level { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid? ParentId { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Administrable reference data: geography, activities and the three status dimensions.</summary>
public sealed class ReferenceService(IAppDbContext db, ICurrentUser user, IAuditService audit, TimeProvider clock)
{
    private void RequireManage()
    {
        if (!user.HasPermission(Permissions.ReferenceManage))
        {
            throw new ForbiddenException();
        }
    }

    // ---------- Geography ----------
    public async Task<IReadOnlyList<GeoDto>> ListGeoAsync(GeoLevel? level, Guid? parentId, bool includeInactive = false, CancellationToken ct = default)
    {
        var q = db.GeographicAreas.AsNoTracking().AsQueryable();
        if (level is not null) q = q.Where(a => a.Level == level);
        if (parentId is not null) q = q.Where(a => a.ParentId == parentId);
        if (!includeInactive) q = q.Where(a => a.IsActive);
        return await q.OrderBy(a => a.Code).ThenBy(a => a.Name).Take(2000)
            .Select(a => new GeoDto(a.Id, a.Level, a.Code, a.Name, a.ParentId, a.Latitude, a.Longitude, a.IsActive))
            .ToListAsync(ct);
    }

    public async Task<GeoDto> SaveGeoAsync(Guid? id, GeoSave input, CancellationToken ct = default)
    {
        RequireManage();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 150) errors.Add("Le nom est obligatoire (150 caractères max).");
        if (input.Code?.Length > 20) errors.Add("Le code ne peut dépasser 20 caractères.");
        if (input.Latitude is not null != (input.Longitude is not null)) errors.Add("Latitude et longitude vont ensemble.");
        if (input.Latitude is { } la && input.Longitude is { } lo && !Businesses.BusinessRules.CoordinatesPlausible(la, lo)) errors.Add("Coordonnées hors d'Algérie.");

        GeographicArea? parent = null;
        if (input.Level == GeoLevel.Wilaya)
        {
            if (input.ParentId is not null) errors.Add("Une wilaya n'a pas de parent.");
        }
        else
        {
            parent = input.ParentId is null ? null : await db.GeographicAreas.FindAsync([input.ParentId], ct);
            if (parent is null || (int)parent.Level != (int)input.Level - 1) errors.Add("Le parent doit être du niveau immédiatement supérieur.");
        }

        if (errors.Count > 0) throw new ValidationException(errors);

        var normalized = TextNormalizer.NormalizeName(input.Name);
        var clash = await db.GeographicAreas.AnyAsync(a => a.Id != id && a.ParentId == input.ParentId && a.Level == input.Level && a.NormalizedName == normalized, ct);
        if (clash) throw new ConflictException($"« {input.Name.Trim()} » existe déjà à ce niveau.");

        GeographicArea area;
        if (id is null)
        {
            area = new GeographicArea { CreatedAt = clock.GetUtcNow().UtcDateTime, Level = input.Level };
            db.GeographicAreas.Add(area);
        }
        else
        {
            area = await db.GeographicAreas.FindAsync([id], ct) ?? throw new NotFoundException();
            if (area.Level != input.Level) throw new ValidationException("Le niveau d'une zone existante ne peut pas changer.");
        }

        area.Name = input.Name.Trim();
        area.NormalizedName = normalized;
        area.Code = input.Code?.Trim() ?? string.Empty;
        area.ParentId = input.ParentId;
        area.Latitude = input.Latitude;
        area.Longitude = input.Longitude;
        area.IsActive = input.IsActive;
        audit.Record(id is null ? "geo.create" : "geo.update", "GeographicArea", area.Id, $"{area.Level}:{area.Name}");
        await db.SaveChangesAsync(ct);
        return new GeoDto(area.Id, area.Level, area.Code, area.Name, area.ParentId, area.Latitude, area.Longitude, area.IsActive);
    }

    public sealed record GeoImportReport(int Created, int Existing, IReadOnlyList<string> Errors);

    /// <summary>
    /// Loads official geography from a CSV (columns: wilaya;daira;commune[;quartier]). Wilayas must already exist (name or 2-digit code);
    /// daïras, communes and quartiers are created when missing. Idempotent: re-importing the same file creates nothing.
    /// </summary>
    public async Task<GeoImportReport> ImportGeographyAsync(Stream csv, CancellationToken ct = default)
    {
        RequireManage();
        using var reader = new StreamReader(csv, new System.Text.UTF8Encoding(false), true);
        using var parser = new CsvHelper.CsvReader(reader, new CsvHelper.Configuration.CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)
        {
            DetectDelimiter = true, DetectDelimiterValues = [",", ";", "\t"], BadDataFound = null, MissingFieldFound = null, HeaderValidated = null, PrepareHeaderForMatch = a => TextNormalizer.Fold(a.Header),
        });
        if (!parser.Read() || !parser.ReadHeader()) throw new ValidationException("Fichier vide.");
        var headers = parser.HeaderRecord!.Select(TextNormalizer.Fold).ToList();
        if (!headers.Contains("wilaya") || !headers.Contains("daira") || !headers.Contains("commune")) throw new ValidationException("Colonnes attendues : wilaya, daira, commune (et optionnellement quartier).");

        var areas = await db.GeographicAreas.ToListAsync(ct);
        var created = 0; var existing = 0; var errors = new List<string>(); var line = 1;
        GeographicArea Ensure(GeoLevel level, string name, GeographicArea parent)
        {
            var key = TextNormalizer.NormalizeName(name);
            var hit = areas.FirstOrDefault(a => a.Level == level && a.ParentId == parent.Id && a.NormalizedName == key);
            if (hit is not null) { existing++; return hit; }
            hit = new GeographicArea { Level = level, Name = name.Trim(), NormalizedName = key, ParentId = parent.Id, Parent = parent, CreatedAt = clock.GetUtcNow().UtcDateTime };
            db.GeographicAreas.Add(hit); areas.Add(hit); created++;
            return hit;
        }

        while (parser.Read())
        {
            line++;
            if (line > 20001) throw new ValidationException("Trop de lignes (20 000 max).");
            string? Get(string col) => parser.TryGetField<string>(col, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
            var w = Get("wilaya"); var d = Get("daira"); var c = Get("commune"); var q = Get("quartier");
            if (w is null || d is null || c is null) { errors.Add($"Ligne {line} : wilaya, daïra et commune sont obligatoires."); continue; }
            var wilaya = areas.FirstOrDefault(a => a.Level == GeoLevel.Wilaya && (a.NormalizedName == TextNormalizer.NormalizeName(w) || a.Code == w.PadLeft(2, '0')));
            if (wilaya is null) { errors.Add($"Ligne {line} : wilaya inconnue « {w} »."); continue; }
            if (d.Length > 150 || c.Length > 150 || q?.Length > 150) { errors.Add($"Ligne {line} : nom trop long."); continue; }
            var daira = Ensure(GeoLevel.Daira, d, wilaya);
            var commune = Ensure(GeoLevel.Commune, c, daira);
            if (q is not null) Ensure(GeoLevel.Quartier, q, commune);
        }

        audit.Record("geo.import", "GeographicArea", null, $"created={created}; existing={existing}; errors={errors.Count}");
        await db.SaveChangesAsync(ct);
        return new GeoImportReport(created, existing, errors.Take(50).ToList());
    }

    // ---------- Activities ----------
    public async Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(Guid? parentId = null, bool rootsOnly = false, bool includeInactive = false, CancellationToken ct = default)
    {
        var q = db.BusinessCategories.AsNoTracking().AsQueryable();
        if (!includeInactive) q = q.Where(c => c.IsActive);
        if (rootsOnly) q = q.Where(c => c.ParentId == null);
        if (parentId is not null) q = q.Where(c => c.ParentId == parentId);
        return await q.OrderBy(c => c.Name).Take(2000).Select(c => new CategoryDto(c.Id, c.Name, c.ParentId, c.IsActive, c.OsmFilter)).ToListAsync(ct);
    }

    public async Task<CategoryDto> SaveCategoryAsync(Guid? id, string name, Guid? parentId, bool isActive, string? osmFilter = null, CancellationToken ct = default)
    {
        RequireManage();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 150) throw new ValidationException("Le nom est obligatoire (150 caractères max).");
        if (parentId is not null)
        {
            var parent = await db.BusinessCategories.FindAsync([parentId], ct);
            if (parent is null || parent.ParentId is not null) throw new ValidationException("Les activités ont deux niveaux : activité puis sous-activité.");
        }

        var filters = (osmFilter ?? "").Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (filters.Any(f => !Collection.OverpassQuery.IsValidFilter(f)) || filters.Count > 10) throw new ValidationException("Filtre OpenStreetMap invalide : utilisez « clé=valeur » séparés par « ; » (ex. amenity=car_rental;shop=car_rental).");
        var normalized = TextNormalizer.NormalizeName(name);
        if (await db.BusinessCategories.AnyAsync(c => c.Id != id && c.ParentId == parentId && c.NormalizedName == normalized, ct))
            throw new ConflictException($"« {name.Trim()} » existe déjà.");

        BusinessCategory cat;
        if (id is null)
        {
            cat = new BusinessCategory { CreatedAt = clock.GetUtcNow().UtcDateTime };
            db.BusinessCategories.Add(cat);
        }
        else
        {
            cat = await db.BusinessCategories.FindAsync([id], ct) ?? throw new NotFoundException();
        }

        cat.Name = name.Trim();
        cat.NormalizedName = normalized;
        cat.ParentId = parentId;
        cat.IsActive = isActive;
        cat.OsmFilter = filters.Count == 0 ? null : string.Join(";", filters);
        audit.Record(id is null ? "category.create" : "category.update", "BusinessCategory", cat.Id, cat.Name);
        await db.SaveChangesAsync(ct);
        return new CategoryDto(cat.Id, cat.Name, cat.ParentId, cat.IsActive, cat.OsmFilter);
    }

    // ---------- Statuses ----------
    public async Task<IReadOnlyList<StatusDto>> ListStatusesAsync(StatusKind? kind = null, bool includeInactive = false, CancellationToken ct = default)
    {
        var q = db.StatusValues.AsNoTracking().AsQueryable();
        if (kind is not null) q = q.Where(s => s.Kind == kind);
        if (!includeInactive) q = q.Where(s => s.IsActive);
        return await q.OrderBy(s => s.Kind).ThenBy(s => s.SortOrder)
            .Select(s => new StatusDto(s.Id, s.Kind, s.Code, s.Label, s.SortOrder, s.IsActive, s.IsSystem)).ToListAsync(ct);
    }

    public async Task<StatusDto> SaveStatusAsync(Guid? id, StatusKind kind, string code, string label, int sortOrder, bool isActive, CancellationToken ct = default)
    {
        RequireManage();
        if (string.IsNullOrWhiteSpace(label) || label.Length > 100) throw new ValidationException("Le libellé est obligatoire (100 caractères max).");
        StatusValue s;
        if (id is null)
        {
            var c = TextNormalizer.ToCode(string.IsNullOrWhiteSpace(code) ? label : code).Replace('-', '_');
            if (c.Length is 0 or > 50) throw new ValidationException("Code invalide.");
            if (await db.StatusValues.AnyAsync(x => x.Kind == kind && x.Code == c, ct)) throw new ConflictException("Ce code existe déjà.");
            s = new StatusValue { Kind = kind, Code = c, CreatedAt = clock.GetUtcNow().UtcDateTime };
            db.StatusValues.Add(s);
        }
        else
        {
            s = await db.StatusValues.FindAsync([id], ct) ?? throw new NotFoundException();
            if (s.IsSystem && !isActive) throw new ValidationException("Un statut système ne peut pas être désactivé (il est utilisé par les règles métier).");
        }

        s.Label = label.Trim();
        s.SortOrder = sortOrder;
        s.IsActive = isActive;
        audit.Record(id is null ? "status.create" : "status.update", "StatusValue", s.Id, $"{s.Kind}:{s.Code}");
        await db.SaveChangesAsync(ct);
        return new StatusDto(s.Id, s.Kind, s.Code, s.Label, s.SortOrder, s.IsActive, s.IsSystem);
    }
}
