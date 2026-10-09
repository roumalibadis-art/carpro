using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using CsvHelper;
using ValidationException = Prospecta.Application.Common.ValidationException;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Businesses;
using Prospecta.Application.Common;
using Prospecta.Application.Duplicates;
using Prospecta.Application.Security;
using Prospecta.Domain.Common;
using Prospecta.Domain.Geography;
using Prospecta.Domain.Imports;

namespace Prospecta.Application.Imports;

public sealed record ImportBatchDto(Guid Id, string FileName, ImportStatus Status, IReadOnlyList<string> Headers, IReadOnlyDictionary<string, string> Mapping,
    int TotalRows, int ValidRows, int InvalidRows, int DuplicateRows, int ImportedRows, DateTime CreatedAt, DateTime? CommittedAt, bool SameFileAlreadyImported);

public sealed record ImportRowDto(int RowNumber, IReadOnlyDictionary<string, string> Raw, ImportRowStatus Status, string? Errors, Guid? PotentialDuplicateOfId);

public sealed class ImportOptions
{
    public long MaxFileBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxRows { get; set; } = 5000;
    public int MaxColumns { get; set; } = 60;
    public int MaxCellLength { get; set; } = 2000;
    public long MaxUncompressedBytes { get; set; } = 100 * 1024 * 1024;
}

/// <summary>Upload → preview → column mapping → validation/duplicates → confirm → report. Nothing is imported before confirmation.</summary>
public sealed class ImportService(
    IAppDbContext db, ICurrentUser user, IAuditService audit, TimeProvider clock,
    BusinessService businesses, DuplicateService duplicates, Microsoft.Extensions.Options.IOptions<ImportOptions> options)
{
    public static readonly IReadOnlyDictionary<string, (string Label, string[] Aliases)> Fields = new Dictionary<string, (string, string[])>
    {
        ["name"] = ("Nom commercial *", ["nom", "name", "nom commercial", "raison sociale", "enseigne", "denomination", "entreprise", "societe"]),
        ["legalName"] = ("Nom légal", ["nom legal", "raison sociale legale", "legal name"]),
        ["category"] = ("Activité", ["activite", "categorie", "secteur", "category", "domaine"]),
        ["subCategory"] = ("Sous-activité", ["sous activite", "sous categorie", "subcategory"]),
        ["wilaya"] = ("Wilaya", ["wilaya", "province"]),
        ["daira"] = ("Daïra", ["daira", "dairat"]),
        ["commune"] = ("Commune", ["commune", "ville", "city"]),
        ["district"] = ("Quartier", ["quartier", "secteur geographique", "zone", "district"]),
        ["address"] = ("Adresse", ["adresse", "address", "localisation"]),
        ["latitude"] = ("Latitude", ["latitude", "lat"]),
        ["longitude"] = ("Longitude", ["longitude", "lng", "lon", "long"]),
        ["plusCode"] = ("Plus Code", ["plus code", "pluscode"]),
        ["phone"] = ("Téléphone", ["telephone", "tel", "phone", "mobile", "numero", "contact"]),
        ["website"] = ("Site web", ["site web", "site", "website", "url", "web"]),
        ["googleMapsUrl"] = ("URL Google Maps", ["google maps", "maps", "url google maps", "lien maps"]),
        ["sourceUrl"] = ("URL source", ["source", "url source", "lien source"]),
        ["description"] = ("Description", ["description", "details", "observations"]),
        ["externalId"] = ("Identifiant fournisseur", ["place id", "place_id", "id fournisseur", "identifiant"]),
    };

    private ImportOptions Opt => options.Value;

    private void RequireImport()
    {
        if (!user.IsAuthenticated || !user.HasPermission(Permissions.BusinessImport)) throw new ForbiddenException();
    }

    private async Task<ImportBatch> OwnBatchAsync(Guid id, CancellationToken ct)
    {
        RequireImport();
        var batch = await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == id, ct) ?? throw new NotFoundException();
        if (batch.UserId != user.Id && !user.HasPermission(Permissions.AuditView)) throw new NotFoundException();
        return batch;
    }

    // ---------- 1. Upload & parse ----------
    public async Task<ImportBatchDto> UploadAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        RequireImport();
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".csv" or ".xlsx")) throw new ValidationException("Format non supporté : utilisez un fichier .csv ou .xlsx.");

        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > Opt.MaxFileBytes) throw new ValidationException($"Fichier trop volumineux (max {Opt.MaxFileBytes / 1024 / 1024} Mo).");
        }

        if (ms.Length == 0) throw new ValidationException("Le fichier est vide.");
        ms.Position = 0;
        var sha = Convert.ToHexString(SHA256.HashData(ms.ToArray()));
        ms.Position = 0;

        List<string> headers;
        List<Dictionary<string, string>> rows;
        try
        {
            (headers, rows) = ext == ".csv" ? ParseCsv(ms) : ParseXlsx(ms);
        }
        catch (AppException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CsvHelperException or FormatException or InvalidOperationException or ArgumentException)
        {
            throw new ValidationException("Fichier illisible ou mal formé.");
        }

        if (headers.Count == 0 || rows.Count == 0) throw new ValidationException("Aucune donnée exploitable (en-têtes ou lignes manquants).");

        var now = clock.GetUtcNow().UtcDateTime;
        var mapping = SuggestMapping(headers);
        var batch = new ImportBatch
        {
            CreatedAt = now, FileName = Path.GetFileName(fileName), FileSha256 = sha, UserId = user.Id!.Value,
            HeadersJson = JsonSerializer.Serialize(headers), MappingJson = JsonSerializer.Serialize(mapping), TotalRows = rows.Count,
        };
        for (var i = 0; i < rows.Count; i++)
            batch.Rows.Add(new ImportRow { RowNumber = i + 2, RawJson = JsonSerializer.Serialize(rows[i]) });
        db.ImportBatches.Add(batch);
        audit.Record("import.upload", "ImportBatch", batch.Id, $"{batch.FileName}; rows={rows.Count}; sha256={sha[..12]}");
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(batch, ct);
    }

    private (List<string>, List<Dictionary<string, string>>) ParseCsv(Stream s)
    {
        using var reader = new StreamReader(s, new UTF8Encoding(false), true, leaveOpen: true);
        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectDelimiter = true, DetectDelimiterValues = [",", ";", "\t", "|"], BadDataFound = null, MissingFieldFound = null,
            HeaderValidated = null, TrimOptions = TrimOptions.Trim, IgnoreBlankLines = true,
        };
        using var csv = new CsvReader(reader, cfg);
        if (!csv.Read() || !csv.ReadHeader()) return ([], []);
        var headers = (csv.HeaderRecord ?? []).Select(h => h.Trim().TrimStart('﻿')).ToList();
        CheckHeaders(headers);
        var rows = new List<Dictionary<string, string>>();
        while (csv.Read())
        {
            if (rows.Count >= Opt.MaxRows) throw new ValidationException($"Trop de lignes (max {Opt.MaxRows}). Scindez le fichier.");
            var row = new Dictionary<string, string>();
            for (var i = 0; i < headers.Count; i++) row[headers[i]] = Clip(csv.GetField(i));
            if (row.Values.Any(v => v.Length > 0)) rows.Add(row);
        }

        return (headers, rows);
    }

    private (List<string>, List<Dictionary<string, string>>) ParseXlsx(Stream s)
    {
        using (var zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true))
        {
            // Guards against decompression bombs before the workbook is loaded in memory.
            if (zip.Entries.Sum(e => e.Length) > Opt.MaxUncompressedBytes) throw new ValidationException("Classeur trop volumineux une fois décompressé.");
        }

        s.Position = 0;
        using var wb = new XLWorkbook(s);
        var ws = wb.Worksheets.FirstOrDefault() ?? throw new ValidationException("Classeur sans feuille.");
        var range = ws.RangeUsed();
        if (range is null) return ([], []);
        var lastCol = Math.Min(range.LastColumn().ColumnNumber(), Opt.MaxColumns + 1);
        var headers = Enumerable.Range(1, lastCol).Select(c => ws.Cell(1, c).GetFormattedString().Trim()).ToList();
        if (headers.Count > Opt.MaxColumns) throw new ValidationException($"Trop de colonnes (max {Opt.MaxColumns}).");
        for (var i = 0; i < headers.Count; i++) if (headers[i].Length == 0) headers[i] = $"Colonne {i + 1}";
        CheckHeaders(headers);
        var lastRow = range.LastRow().RowNumber();
        if (lastRow - 1 > Opt.MaxRows) throw new ValidationException($"Trop de lignes (max {Opt.MaxRows}). Scindez le fichier.");
        var rows = new List<Dictionary<string, string>>();
        for (var r = 2; r <= lastRow; r++)
        {
            var row = new Dictionary<string, string>();
            for (var c = 1; c <= headers.Count; c++) row[headers[c - 1]] = Clip(ws.Cell(r, c).GetFormattedString());
            if (row.Values.Any(v => v.Length > 0)) rows.Add(row);
        }

        return (headers, rows);
    }

    private void CheckHeaders(List<string> headers)
    {
        if (headers.Count > Opt.MaxColumns) throw new ValidationException($"Trop de colonnes (max {Opt.MaxColumns}).");
        var dup = headers.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new ValidationException($"En-tête dupliqué : « {dup.Key} ».");
    }

    private string Clip(string? v)
    {
        v = (v ?? string.Empty).Trim();
        return v.Length > Opt.MaxCellLength ? v[..Opt.MaxCellLength] : v;
    }

    public static Dictionary<string, string> SuggestMapping(IEnumerable<string> headers)
    {
        var map = new Dictionary<string, string>();
        var folded = headers.Select(h => (h, f: TextNormalizer.Fold(h))).ToList();
        foreach (var (field, (_, aliases)) in Fields)
        {
            var hit = folded.FirstOrDefault(x => aliases.Contains(x.f) && !map.ContainsValue(x.h));
            if (hit.h is not null) map[field] = hit.h;
        }

        return map;
    }

    // ---------- 2. Mapping + validation ----------
    public async Task<ImportBatchDto> MapAsync(Guid batchId, IReadOnlyDictionary<string, string> mapping, CancellationToken ct = default)
    {
        var batch = await OwnBatchAsync(batchId, ct);
        if (batch.Status is ImportStatus.Committed or ImportStatus.Cancelled) throw new ConflictException("Cet import est terminé.");
        var headers = JsonSerializer.Deserialize<List<string>>(batch.HeadersJson)!;
        var clean = new Dictionary<string, string>();
        foreach (var (field, column) in mapping)
        {
            if (string.IsNullOrWhiteSpace(column)) continue;
            if (!Fields.ContainsKey(field)) throw new ValidationException($"Champ inconnu : {field}.");
            if (!headers.Contains(column)) throw new ValidationException($"Colonne inconnue : {column}.");
            clean[field] = column;
        }

        if (!clean.ContainsKey("name")) throw new ValidationException("Associez au minimum la colonne « Nom commercial ».");
        if (clean.Values.GroupBy(c => c).Any(g => g.Count() > 1)) throw new ValidationException("Une colonne ne peut alimenter qu'un seul champ.");
        batch.MappingJson = JsonSerializer.Serialize(clean);

        var refs = await LoadRefsAsync(ct);
        var rows = await db.ImportRows.Where(r => r.BatchId == batchId).OrderBy(r => r.RowNumber).ToListAsync(ct);
        var seen = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            var (input, ext, errors) = BuildInput(row, clean, refs);
            row.Errors = null; row.PotentialDuplicateOfId = null;
            if (errors.Count > 0)
            {
                row.Status = ImportRowStatus.Invalid;
                row.Errors = string.Join(" ; ", errors);
                continue;
            }

            var key = TextNormalizer.NormalizeName(input!.Name) + "|" + (AlgerianPhone.Normalize(input.Phone) ?? input.CommuneId?.ToString() ?? "");
            if (seen.TryGetValue(key, out var firstRow))
            {
                row.Status = ImportRowStatus.PotentialDuplicate;
                row.Errors = $"Doublon probable dans le fichier (ligne {firstRow}).";
                continue;
            }

            seen[key] = row.RowNumber;
            var profile = new DupProfile(TextNormalizer.NormalizeName(input.Name), AlgerianPhone.Normalize(input.Phone), DuplicateDetector.WebsiteKey(input.Website),
                TextNormalizer.Fold(input.Address), input.CommuneId, input.Latitude, input.Longitude,
                ext is null ? new HashSet<string>() : [$"import:{batch.FileName}:{ext}"]);
            var matches = await duplicates.FindMatchesAsync(profile, input.WilayaId, null, ct);
            if (matches.Count > 0)
            {
                row.Status = ImportRowStatus.PotentialDuplicate;
                row.PotentialDuplicateOfId = matches[0].BusinessId;
                row.Errors = $"Ressemble à « {matches[0].Name} » ({matches[0].Score:P0} : {string.Join(", ", matches[0].Reasons)}).";
            }
            else
            {
                row.Status = ImportRowStatus.Valid;
            }
        }

        batch.ValidRows = rows.Count(r => r.Status == ImportRowStatus.Valid);
        batch.InvalidRows = rows.Count(r => r.Status == ImportRowStatus.Invalid);
        batch.DuplicateRows = rows.Count(r => r.Status == ImportRowStatus.PotentialDuplicate);
        batch.Status = ImportStatus.Mapped;
        audit.Record("import.map", "ImportBatch", batch.Id, $"valid={batch.ValidRows}; invalid={batch.InvalidRows}; duplicates={batch.DuplicateRows}");
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(batch, ct);
    }

    private sealed record Refs(List<GeographicArea> Areas, List<BusinessCategory> Categories);

    private async Task<Refs> LoadRefsAsync(CancellationToken ct) =>
        new(await db.GeographicAreas.AsNoTracking().Where(a => a.IsActive).ToListAsync(ct), await db.BusinessCategories.AsNoTracking().Where(c => c.IsActive).ToListAsync(ct));

    private static (BusinessInput? Input, string? ExternalId, List<string> Errors) BuildInput(ImportRow row, Dictionary<string, string> mapping, Refs refs)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(row.RawJson)!;
        string? Get(string field) => mapping.TryGetValue(field, out var col) && raw.TryGetValue(col, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        var errors = new List<string>();
        var input = new BusinessInput
        {
            Name = Get("name") ?? string.Empty, LegalName = Get("legalName"), Description = Get("description"), Address = Get("address"),
            PlusCode = Get("plusCode"), Website = Get("website"), GoogleMapsUrl = Get("googleMapsUrl"), SourceUrl = Get("sourceUrl"), Phone = Get("phone"),
        };
        if (input.Name.Length == 0) errors.Add("Nom commercial manquant.");
        else if (input.Name.Length > 200) errors.Add("Nom trop long (200 max).");
        if (input.Phone is not null && !AlgerianPhone.IsValid(input.Phone)) errors.Add($"Téléphone invalide : « {input.Phone} ».");
        if (input.Website is not null && TextNormalizer.WebsiteHost(input.Website) is null) errors.Add("Site web invalide.");
        if (input.GoogleMapsUrl is not null && TextNormalizer.WebsiteHost(input.GoogleMapsUrl) is null) errors.Add("URL Google Maps invalide.");
        if (input.SourceUrl is not null && TextNormalizer.WebsiteHost(input.SourceUrl) is null) errors.Add("URL source invalide.");

        var latS = Get("latitude"); var lonS = Get("longitude");
        if (latS is not null || lonS is not null)
        {
            if (latS is null || lonS is null || !BusinessRules.TryParseDouble(latS, out var lat) || !BusinessRules.TryParseDouble(lonS, out var lon)) errors.Add("Latitude/longitude invalides ou incomplètes.");
            else if (!BusinessRules.CoordinatesPlausible(lat, lon)) errors.Add("Coordonnées hors d'Algérie.");
            else { input.Latitude = lat; input.Longitude = lon; }
        }

        // Geography by name (accent/case-insensitive); each level must belong to the previous one.
        GeographicArea? wilaya = null, daira = null, commune = null, district = null;
        string Key(string s) => TextNormalizer.NormalizeName(s);
        var byParent = refs.Areas.ToLookup(a => a.ParentId);
        if (Get("wilaya") is { } w)
        {
            var wk = Key(w.Replace("wilaya de ", "", StringComparison.OrdinalIgnoreCase).Replace("wilaya d'", "", StringComparison.OrdinalIgnoreCase));
            wilaya = refs.Areas.FirstOrDefault(a => a.Level == GeoLevel.Wilaya && (a.NormalizedName == wk || a.Code == w.Trim().PadLeft(2, '0')));
            if (wilaya is null) errors.Add($"Wilaya inconnue : « {w} ».");
        }

        IEnumerable<GeographicArea> Within(GeoLevel level, GeographicArea? parent, GeographicArea? grand)
        {
            if (parent is not null) return byParent[parent.Id].Where(a => a.Level == level);
            if (grand is not null) return byParent[grand.Id].SelectMany(d => byParent[d.Id]).Where(a => a.Level == level);
            return refs.Areas.Where(a => a.Level == level);
        }

        if (Get("daira") is { } d && errors.Count == 0)
        {
            var hits = Within(GeoLevel.Daira, wilaya, null).Where(a => a.NormalizedName == Key(d)).ToList();
            if (hits.Count == 1) daira = hits[0]; else errors.Add(hits.Count == 0 ? $"Daïra inconnue : « {d} »." : $"Daïra ambiguë : « {d} », précisez la wilaya.");
        }

        if (Get("commune") is { } c && errors.Count == 0)
        {
            var hits = (daira is not null ? Within(GeoLevel.Commune, daira, null) : Within(GeoLevel.Commune, null, wilaya)).Where(a => a.NormalizedName == Key(c)).ToList();
            if (wilaya is null && daira is null) hits = refs.Areas.Where(a => a.Level == GeoLevel.Commune && a.NormalizedName == Key(c)).ToList();
            if (hits.Count == 1) commune = hits[0]; else errors.Add(hits.Count == 0 ? $"Commune inconnue : « {c} »." : $"Commune ambiguë : « {c} », précisez la wilaya ou la daïra.");
        }

        if (Get("district") is { } q && errors.Count == 0)
        {
            district = commune is null ? null : byParent[commune.Id].FirstOrDefault(a => a.Level == GeoLevel.Quartier && a.NormalizedName == Key(q));
            if (district is null) errors.Add($"Quartier inconnu pour cette commune : « {q} ».");
        }

        input.WilayaId = wilaya?.Id; input.DairaId = daira?.Id; input.CommuneId = commune?.Id; input.DistrictId = district?.Id;

        BusinessCategory? cat = null;
        if (Get("category") is { } cn)
        {
            cat = refs.Categories.FirstOrDefault(x => x.ParentId == null && x.NormalizedName == Key(cn));
            if (cat is null) errors.Add($"Activité inconnue : « {cn} ».");
        }

        if (Get("subCategory") is { } sn)
        {
            var sub = refs.Categories.FirstOrDefault(x => x.ParentId != null && x.NormalizedName == Key(sn) && (cat == null || x.ParentId == cat.Id));
            if (sub is null) errors.Add($"Sous-activité inconnue : « {sn} ».");
            else { input.SubCategoryId = sub.Id; cat ??= refs.Categories.FirstOrDefault(x => x.Id == sub.ParentId); }
        }

        input.CategoryId = cat?.Id;
        return (errors.Count == 0 ? input : null, Get("externalId"), errors);
    }

    // ---------- 3. Commit ----------
    public async Task<ImportBatchDto> CommitAsync(Guid batchId, bool includePotentialDuplicates, CancellationToken ct = default)
    {
        var batch = await OwnBatchAsync(batchId, ct);
        if (batch.Status != ImportStatus.Mapped) throw new ConflictException("Associez les colonnes et validez l'aperçu avant de confirmer.");
        var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(batch.MappingJson)!;
        var refs = await LoadRefsAsync(ct);
        var statuses = includePotentialDuplicates ? new[] { ImportRowStatus.Valid, ImportRowStatus.PotentialDuplicate } : [ImportRowStatus.Valid];

        await using var tx = await UnitOfWork.BeginAsync(db.Database, ct);
        var rows = await db.ImportRows.Where(r => r.BatchId == batchId && statuses.Contains(r.Status)).OrderBy(r => r.RowNumber).ToListAsync(ct);
        var created = new List<(ImportRow Row, Domain.Businesses.Business Biz)>();
        foreach (var row in rows)
        {
            var (input, ext, errors) = BuildInput(row, mapping, refs);
            if (input is null || (await businesses.ValidateAsync(input, ct)) is { Count: > 0 } more)
            {
                row.Status = ImportRowStatus.Invalid;
                row.Errors = string.Join(" ; ", errors.Count > 0 ? errors : ["Données devenues invalides depuis l'aperçu."]);
                continue;
            }

            var biz = await businesses.BuildNewAsync(input, FieldOrigin.External,
                new BusinessService.SourceInfo(SourceType.FileImport, $"import:{batch.FileName}", ext is null ? null : $"import:{batch.FileName}:{ext}", input.SourceUrl, batch.Id), ct);
            created.Add((row, biz));
        }

        await db.SaveChangesAsync(ct);
        foreach (var (row, biz) in created)
        {
            row.ImportedBusinessId = biz.Id;
            row.Status = ImportRowStatus.Imported;
        }

        batch.ImportedRows = created.Count;
        batch.InvalidRows = await db.ImportRows.CountAsync(r => r.BatchId == batchId && r.Status == ImportRowStatus.Invalid, ct);
        batch.DuplicateRows = await db.ImportRows.CountAsync(r => r.BatchId == batchId && r.Status == ImportRowStatus.PotentialDuplicate, ct);
        batch.Status = ImportStatus.Committed;
        batch.CommittedAt = clock.GetUtcNow().UtcDateTime;
        audit.Record("import.commit", "ImportBatch", batch.Id, $"imported={created.Count}; invalid={batch.InvalidRows}; skipped_duplicates={batch.DuplicateRows}");
        await db.SaveChangesAsync(ct);
        foreach (var (_, biz) in created) await duplicates.DetectAsync(biz, ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(batch, ct);
    }

    public async Task CancelAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await OwnBatchAsync(batchId, ct);
        if (batch.Status == ImportStatus.Committed) throw new ConflictException("Un import confirmé ne peut pas être annulé.");
        batch.Status = ImportStatus.Cancelled;
        audit.Record("import.cancel", "ImportBatch", batch.Id);
        await db.SaveChangesAsync(ct);
    }

    // ---------- Reads ----------
    public async Task<ImportBatchDto> GetAsync(Guid id, CancellationToken ct = default) => await ToDtoAsync(await OwnBatchAsync(id, ct), ct);

    public async Task<PagedResult<ImportRowDto>> RowsAsync(Guid id, ImportRowStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        await OwnBatchAsync(id, ct);
        (page, pageSize) = Paging.Clamp(page, pageSize);
        var q = db.ImportRows.AsNoTracking().Where(r => r.BatchId == id);
        if (status is not null) q = q.Where(r => r.Status == status);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(r => r.RowNumber).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<ImportRowDto>(rows.Select(r => new ImportRowDto(r.RowNumber, JsonSerializer.Deserialize<Dictionary<string, string>>(r.RawJson)!, r.Status, r.Errors, r.PotentialDuplicateOfId)).ToList(), total, page, pageSize);
    }

    public async Task<IReadOnlyList<ImportBatchDto>> ListAsync(CancellationToken ct = default)
    {
        RequireImport();
        var q = db.ImportBatches.AsNoTracking().AsQueryable();
        if (!user.HasPermission(Permissions.AuditView)) q = q.Where(b => b.UserId == user.Id);
        var batches = await q.OrderByDescending(b => b.CreatedAt).Take(50).ToListAsync(ct);
        var list = new List<ImportBatchDto>();
        foreach (var b in batches) list.Add(await ToDtoAsync(b, ct));
        return list;
    }

    private async Task<ImportBatchDto> ToDtoAsync(ImportBatch b, CancellationToken ct) => new(
        b.Id, b.FileName, b.Status, JsonSerializer.Deserialize<List<string>>(b.HeadersJson)!, JsonSerializer.Deserialize<Dictionary<string, string>>(b.MappingJson)!,
        b.TotalRows, b.ValidRows, b.InvalidRows, b.DuplicateRows, b.ImportedRows, b.CreatedAt, b.CommittedAt,
        b.Status != ImportStatus.Committed && await db.ImportBatches.AnyAsync(x => x.FileSha256 == b.FileSha256 && x.Status == ImportStatus.Committed && x.Id != b.Id, ct));
}
