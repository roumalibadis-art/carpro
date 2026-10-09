using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Businesses;
using Prospecta.Application.Common;
using Prospecta.Application.Prospecting;
using Prospecta.Application.Security;
using Prospecta.Domain.Common;
using Prospecta.Domain.Prospecting;

namespace Prospecta.Application.Reporting;

public sealed record ReportSummary(Guid Id, ReportType Type, string Title, string Owner, Guid OwnerUserId, bool Shared, DateTime GeneratedAt, string? ValidatedBy, DateTime? ValidatedAt, bool Mine);

public sealed record ReportView(ReportSummary Summary, ReportParameters Parameters, ReportDocument Document, bool CanValidate, bool CanShare, Guid? SourceReportId);

public sealed class ReportService(IAppDbContext db, ICurrentUser user, ReportBuilders builders, TeamScope team, IReportPdfRenderer pdf, IAuditService audit, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private void RequireCreate()
    {
        if (!user.IsAuthenticated || !user.HasPermission(Permissions.ReportCreate)) throw new ForbiddenException();
    }

    private async Task<ReportDocument> BuildAsync(ReportType type, ReportParameters p, CancellationToken ct)
    {
        RequireCreate();
        if (p.Title?.Length > 250) throw new ValidationException("Titre trop long (250 caractères max).");
        foreach (var (label, v) in new[] { ("Objectif", p.Objective), ("Retours positifs", p.PositiveFeedback), ("Difficultés", p.Difficulties), ("Améliorations", p.Improvements) })
            if (v?.Length > 4000) throw new ValidationException($"{label} : 4000 caractères maximum.");
        if (p.UserId is { } uid && uid != user.Id && !(await team.IsInTeamAsync(uid, ct))) throw new NotFoundException();
        return type switch
        {
            ReportType.MarketStudy => await builders.MarketStudyAsync(p, ct),
            ReportType.OutingBalance => await builders.OutingBalanceAsync(p, ct),
            ReportType.Individual => await builders.IndividualAsync(p, ct),
            ReportType.Manager => await builders.ManagerAsync(p, ct),
            _ => throw new ValidationException("Type de rapport inconnu."),
        };
    }

    /// <summary>Computes a report for review without saving it.</summary>
    public Task<ReportDocument> PreviewAsync(ReportType type, ReportParameters p, CancellationToken ct = default) => BuildAsync(type, p, ct);

    /// <summary>Generates and stores the report with an immutable snapshot of its figures.</summary>
    public async Task<ReportSummary> SaveAsync(ReportType type, ReportParameters p, bool share, Guid? sourceId = null, CancellationToken ct = default)
    {
        var doc = await BuildAsync(type, p, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var r = new Report
        {
            Type = type, Title = doc.Title, OwnerUserId = user.Id!.Value, ParametersJson = JsonSerializer.Serialize(p, Json), Shared = share, SourceReportId = sourceId, CreatedAt = now,
        };
        r.Snapshots.Add(new ReportSnapshot { ReportId = r.Id, ContentJson = JsonSerializer.Serialize(doc, Json), CreatedAt = now });
        db.Reports.Add(r);
        audit.Record("report.create", "Report", r.Id, $"{type}; shared={share}; source={sourceId}");
        await db.SaveChangesAsync(ct);
        return (await SummariesAsync(db.Reports.Where(x => x.Id == r.Id), ct)).First();
    }

    private bool CanRead(Report r, bool ownerInTeam) =>
        r.OwnerUserId == user.Id || (r.Shared && (user.HasPermission(Permissions.ReportViewAll) || (user.HasPermission(Permissions.ReportViewTeam) && ownerInTeam)));

    private async Task<Report> LoadAsync(Guid id, bool withSnapshot, CancellationToken ct)
    {
        if (!user.IsAuthenticated) throw new ForbiddenException();
        var q = db.Reports.AsQueryable();
        if (withSnapshot) q = q.Include(r => r.Snapshots);
        var r = await q.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        // A report you may not read does not exist for you (no id probing).
        if (!CanRead(r, r.Shared && await team.IsInTeamAsync(r.OwnerUserId, ct))) throw new NotFoundException();
        return r;
    }

    public async Task<PagedResult<ReportSummary>> ListAsync(bool teamReports, ReportType? type, int page, int pageSize, CancellationToken ct = default)
    {
        RequireCreate();
        (page, pageSize) = Paging.Clamp(page, pageSize);
        var me = user.Id!.Value;
        IQueryable<Report> q;
        if (!teamReports) q = db.Reports.Where(r => r.OwnerUserId == me);
        else if (user.HasPermission(Permissions.ReportViewAll)) q = db.Reports.Where(r => r.Shared && r.OwnerUserId != me);
        else if (user.HasPermission(Permissions.ReportViewTeam))
        {
            var ids = await team.UserIdsAsync(ct) ?? [];
            q = db.Reports.Where(r => r.Shared && r.OwnerUserId != me && ids.Contains(r.OwnerUserId));
        }
        else throw new ForbiddenException();
        if (type is not null) q = q.Where(r => r.Type == type);
        var total = await q.CountAsync(ct);
        var items = await SummariesAsync(q.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize), ct);
        return new PagedResult<ReportSummary>(items, total, page, pageSize);
    }

    private async Task<List<ReportSummary>> SummariesAsync(IQueryable<Report> q, CancellationToken ct)
    {
        var me = user.Id;
        var rows = await q.AsNoTracking().Select(r => new
        {
            r.Id, r.Type, r.Title, r.OwnerUserId, r.Shared, r.CreatedAt, r.ValidatedAt,
            Owner = db.AppUsers.Where(u => u.Id == r.OwnerUserId).Select(u => u.FullName).FirstOrDefault(),
            Validator = db.AppUsers.Where(u => u.Id == r.ValidatedByUserId).Select(u => u.FullName).FirstOrDefault(),
        }).ToListAsync(ct);
        return rows.Select(r => new ReportSummary(r.Id, r.Type, r.Title, r.Owner ?? "?", r.OwnerUserId, r.Shared, r.CreatedAt, r.Validator, r.ValidatedAt, r.OwnerUserId == me)).ToList();
    }

    /// <summary>Reads the stored snapshot: never recomputed, whatever happened to the businesses since.</summary>
    public async Task<ReportView> GetAsync(Guid id, CancellationToken ct = default)
    {
        var r = await LoadAsync(id, true, ct);
        var doc = JsonSerializer.Deserialize<ReportDocument>(r.Snapshots.OrderByDescending(s => s.CreatedAt).First().ContentJson, Json)!;
        var summary = (await SummariesAsync(db.Reports.Where(x => x.Id == id), ct)).First();
        // Validation is a review stamp on top of the frozen figures.
        if (r.Type == ReportType.OutingBalance || r.ValidatedAt is not null)
        {
            doc.Meta.RemoveAll(m => m.Key is "Vérificateur" or "Date de validation");
            doc.Meta.Add(new("Vérificateur", summary.ValidatedBy ?? Doc.NotProvided));
            doc.Meta.Add(new("Date de validation", summary.ValidatedAt is null ? Doc.NotProvided : Dates.UtcToLocal(summary.ValidatedAt.Value).ToString("dd/MM/yyyy HH:mm")));
            if (!string.IsNullOrWhiteSpace(r.ValidationNote)) doc.Meta.Add(new("Note de validation", r.ValidationNote!));
        }

        var canValidate = r.OwnerUserId != user.Id && r.Shared && r.ValidatedAt is null && (user.HasPermission(Permissions.ReportViewAll) || user.HasPermission(Permissions.ReportViewTeam));
        return new ReportView(summary, JsonSerializer.Deserialize<ReportParameters>(r.ParametersJson, Json) ?? new(), doc, canValidate, r.OwnerUserId == user.Id, r.SourceReportId);
    }

    public async Task SetSharedAsync(Guid id, bool shared, CancellationToken ct = default)
    {
        var r = await LoadAsync(id, false, ct);
        if (r.OwnerUserId != user.Id) throw new NotFoundException();
        if (!shared && r.ValidatedAt is not null) throw new ConflictException("Un rapport validé ne peut plus être retiré du partage.");
        r.Shared = shared;
        audit.Record("report.share", "Report", id, shared.ToString());
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A reviewer in the author's hierarchy stamps a shared report as verified (name + date).</summary>
    public async Task ValidateAsync(Guid id, string? note, CancellationToken ct = default)
    {
        var r = await LoadAsync(id, false, ct);
        if (r.OwnerUserId == user.Id) throw new ForbiddenException("Vous ne pouvez pas valider votre propre rapport.");
        if (!r.Shared) throw new NotFoundException();
        if (r.ValidatedAt is not null) throw new ConflictException("Ce rapport est déjà validé.");
        if (note?.Length > 500) throw new ValidationException("Note trop longue (500 caractères max).");
        r.ValidatedByUserId = user.Id; r.ValidatedAt = clock.GetUtcNow().UtcDateTime; r.ValidationNote = note?.Trim();
        audit.Record("report.validate", "Report", id, note);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>New analysis from the same parameters (fresh figures, new report); the original stays untouched.</summary>
    public async Task<ReportSummary> DuplicateAsync(Guid id, ReportParameters? overrides, CancellationToken ct = default)
    {
        var r = await LoadAsync(id, false, ct);
        var p = JsonSerializer.Deserialize<ReportParameters>(r.ParametersJson, Json) ?? new();
        if (overrides is not null)
        {
            // Only the values the caller supplies change; everything else is reused.
            p.Title = overrides.Title ?? p.Title; p.From = overrides.From ?? p.From; p.To = overrides.To ?? p.To; p.CampaignId = overrides.CampaignId ?? p.CampaignId;
            p.UserId = overrides.UserId ?? p.UserId; p.CommuneId = overrides.CommuneId ?? p.CommuneId; p.CategoryId = overrides.CategoryId ?? p.CategoryId; p.OutingId = overrides.OutingId ?? p.OutingId;
            p.Filter = overrides.Filter ?? p.Filter; p.Objective = overrides.Objective ?? p.Objective; p.PositiveFeedback = overrides.PositiveFeedback ?? p.PositiveFeedback;
            p.Difficulties = overrides.Difficulties ?? p.Difficulties; p.Improvements = overrides.Improvements ?? p.Improvements;
        }

        if (string.IsNullOrWhiteSpace(overrides?.Title)) p.Title = (p.Title ?? r.Title) + " (copie)";
        return await SaveAsync(r.Type, p, false, r.Id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var r = await LoadAsync(id, false, ct);
        if (r.OwnerUserId != user.Id) throw new NotFoundException();
        if (r.ValidatedAt is not null) throw new ConflictException("Un rapport validé est conservé.");
        db.Reports.Remove(r);
        audit.Record("report.delete", "Report", id, r.Title);
        await db.SaveChangesAsync(ct);
    }

    // ---------- Exports ----------
    public async Task<ExportFile> ExportAsync(Guid id, string format, CancellationToken ct = default)
    {
        var view = await GetAsync(id, ct);
        audit.Record("report.export", "Report", id, format);
        await db.SaveChangesAsync(ct);
        return Export(view.Document, format, view.Summary.Title);
    }

    public ExportFile Export(ReportDocument doc, string format, string name)
    {
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (safe.Length > 60) safe = safe[..60];
        return format.ToLowerInvariant() switch
        {
            "pdf" => new ExportFile(pdf.Render(doc), "application/pdf", $"{safe}.pdf"),
            "xlsx" => new ExportFile(ToXlsx(doc), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{safe}.xlsx"),
            _ => throw new ValidationException("Format d'export inconnu (pdf ou xlsx)."),
        };
    }

    /// <summary>One summary sheet plus one sheet per table; cells are written as text (no formula evaluation).</summary>
    public static byte[] ToXlsx(ReportDocument doc)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Résumé");
        var row = 1;
        ws.Cell(row++, 1).SetValue(Businesses.ExportService.Safe(doc.Title)).Style.Font.Bold = true;
        if (doc.Subtitle is not null) ws.Cell(row++, 1).SetValue(Businesses.ExportService.Safe(doc.Subtitle));
        row++;
        foreach (var m in doc.Meta) { ws.Cell(row, 1).SetValue(Businesses.ExportService.Safe(m.Key)).Style.Font.Bold = true; ws.Cell(row++, 2).SetValue(Businesses.ExportService.Safe(m.Value)); }
        row++;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Résumé" };
        foreach (var s in doc.Sections)
        {
            ws.Cell(row++, 1).SetValue(Businesses.ExportService.Safe(s.Heading + (s.Kind == SectionKind.Analysis ? " [analyse générée]" : ""))).Style.Font.Bold = true;
            foreach (var b in s.Blocks)
            {
                switch (b.Type)
                {
                    case "p" or "note": ws.Cell(row++, 1).SetValue(Businesses.ExportService.Safe(b.Text)); break;
                    case "list": foreach (var i in b.Items) ws.Cell(row++, 1).SetValue(Businesses.ExportService.Safe("• " + i)); break;
                    case "kv": foreach (var kv in b.Pairs) { ws.Cell(row, 1).SetValue(Businesses.ExportService.Safe(kv.Key)); ws.Cell(row++, 2).SetValue(Businesses.ExportService.Safe(kv.Value)); } break;
                    case "table":
                        var baseName = new string((b.Caption ?? s.Heading).Where(c => !"[]:*?/\\".Contains(c)).ToArray());
                        baseName = baseName.Length > 28 ? baseName[..28] : baseName;
                        var name = baseName; var n = 2;
                        while (!used.Add(name)) name = (baseName.Length > 25 ? baseName[..25] : baseName) + " " + n++;
                        var t = wb.Worksheets.Add(name);
                        for (var c = 0; c < b.Headers.Count; c++) { t.Cell(1, c + 1).SetValue(Businesses.ExportService.Safe(b.Headers[c])).Style.Font.Bold = true; }
                        for (var r = 0; r < b.Rows.Count; r++) for (var c = 0; c < b.Rows[r].Count; c++) t.Cell(r + 2, c + 1).SetValue(Businesses.ExportService.Safe(b.Rows[r][c]));
                        t.SheetView.FreezeRows(1); t.Columns().AdjustToContents(1, Math.Min(b.Rows.Count + 1, 100));
                        ws.Cell(row++, 1).SetValue($"Tableau « {b.Caption ?? s.Heading} » : feuille « {name} »");
                        break;
                }
            }

            row++;
        }

        ws.Column(1).Width = 60; ws.Column(2).Width = 60;
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
