using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Common;
using Prospecta.Application.Security;
using ValidationException = Prospecta.Application.Common.ValidationException;

namespace Prospecta.Application.Businesses;

public sealed record ExportFile(byte[] Content, string ContentType, string FileName);

/// <summary>Exports exactly what the caller may see (same scope + filters as the list); never more.</summary>
public sealed class ExportService(IAppDbContext db, BusinessService businesses, ICurrentUser user, IAuditService audit, TimeProvider clock)
{
    public const int MaxRows = 20000;

    private static readonly string[] Header =
    [
        "Identifiant", "Nom commercial", "Nom légal", "Activité", "Sous-activité", "Wilaya", "Daïra", "Commune", "Quartier", "Adresse",
        "Latitude", "Longitude", "Téléphone", "Site web", "URL Google Maps", "Statut recensement", "Statut traitement", "Résultat commercial",
        "Priorité", "Complétude %", "Confiance", "Date de collecte", "Dernière vérification", "Responsables",
    ];

    public async Task<ExportFile> ExportAsync(BusinessFilter filter, string format, CancellationToken ct = default)
    {
        if (!user.HasPermission(Permissions.BusinessExport)) throw new ForbiddenException();
        var q = businesses.Filtered(filter);
        var total = await q.CountAsync(ct);
        if (total > MaxRows) throw new ValidationException($"Trop de résultats ({total}). Affinez les filtres (maximum {MaxRows} lignes).");

        var rows = await q.OrderBy(b => b.NormalizedName).ThenBy(b => b.Id).AsNoTracking().Select(b => new
        {
            b.Id, b.Name, b.LegalName, Cat = b.Category!.Name, Sub = b.SubCategory!.Name, W = b.Wilaya!.Name, D = b.Daira!.Name, C = b.Commune!.Name,
            Q = b.District!.Name, b.Address, b.Latitude, b.Longitude, b.Phone, b.Website, b.GoogleMapsUrl,
            Census = b.CensusStatus!.Label, Proc = b.ProcessingStatus!.Label, Out = b.OutcomeStatus!.Label, b.Priority, b.CompletenessPercent,
            b.Confidence, b.CollectedAt, b.LastVerifiedAt,
        }).ToListAsync(ct);

        var resp = await businesses.ResponsibleAsync(db.BusinessAssignments.Where(a => q.Select(b => b.Id).Contains(a.BusinessId)), ct);
        var table = rows.Select(r => new string?[]
        {
            r.Id.ToString(), r.Name, r.LegalName, r.Cat, r.Sub, r.W, r.D, r.C, r.Q, r.Address,
            BusinessRules.FormatDouble(r.Latitude), BusinessRules.FormatDouble(r.Longitude), r.Phone, r.Website, r.GoogleMapsUrl,
            r.Census, r.Proc, r.Out, r.Priority.ToString(), r.CompletenessPercent.ToString(CultureInfo.InvariantCulture), r.Confidence.ToString(),
            r.CollectedAt.ToString("yyyy-MM-dd"), r.LastVerifiedAt?.ToString("yyyy-MM-dd"), string.Join(", ", resp.GetValueOrDefault(r.Id) ?? []),
        }).ToList();

        audit.Record("business.export", "Business", null, $"format={format}; rows={table.Count}; filter={BusinessService.FilterToJson(filter)}");
        await db.SaveChangesAsync(ct);
        var stamp = clock.GetUtcNow().ToString("yyyyMMdd-HHmm");
        return format.ToLowerInvariant() switch
        {
            "csv" => new ExportFile(ToCsv(table), "text/csv; charset=utf-8", $"entreprises-{stamp}.csv"),
            "xlsx" => new ExportFile(ToXlsx(table), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"entreprises-{stamp}.xlsx"),
            _ => throw new ValidationException("Format d'export inconnu (csv ou xlsx)."),
        };
    }

    /// <summary>Neutralizes spreadsheet formula injection: a cell starting with = + - @ is prefixed with an apostrophe.</summary>
    public static string Safe(string? v)
    {
        if (string.IsNullOrEmpty(v)) return string.Empty;
        return v[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + v : v;
    }

    private static byte[] ToCsv(List<string?[]> table)
    {
        using var ms = new MemoryStream();
        using (var w = new StreamWriter(ms, new UTF8Encoding(true), leaveOpen: true))
        using (var csv = new CsvWriter(w, new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = ";" }))
        {
            foreach (var h in Header) csv.WriteField(h);
            csv.NextRecord();
            foreach (var row in table)
            {
                foreach (var cell in row) csv.WriteField(Safe(cell));
                csv.NextRecord();
            }
        }

        return ms.ToArray();
    }

    private static byte[] ToXlsx(List<string?[]> table)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Entreprises");
        for (var c = 0; c < Header.Length; c++) ws.Cell(1, c + 1).Value = Header[c];
        ws.Row(1).Style.Font.Bold = true;
        for (var r = 0; r < table.Count; r++)
            for (var c = 0; c < Header.Length; c++)
                ws.Cell(r + 2, c + 1).SetValue(Safe(table[r][c])); // always text: no formula evaluation, phones keep their zero
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(1, Math.Min(table.Count + 1, 200));
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
