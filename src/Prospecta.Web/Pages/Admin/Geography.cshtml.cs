using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Common;
using Prospecta.Application.Reference;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Admin;

public class GeographyModel(ReferenceService service) : AppPage
{
    /// <summary>Drill-down: the deepest selected node decides which level is listed.</summary>
    [BindProperty(SupportsGet = true)] public Guid? WilayaId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? DairaId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? CommuneId { get; set; }
    public GeoLevel ListLevel => CommuneId is not null ? GeoLevel.Quartier : DairaId is not null ? GeoLevel.Commune : WilayaId is not null ? GeoLevel.Daira : GeoLevel.Wilaya;
    public Guid? ParentId => CommuneId ?? DairaId ?? WilayaId;
    public IReadOnlyList<GeoDto> Items { get; private set; } = [];
    public List<(string Label, string Url)> Crumbs { get; } = [];

    public async Task OnGetAsync()
    {
        Items = await service.ListGeoAsync(ListLevel, ParentId, true);
        Crumbs.Add(("Wilayas", "/Admin/Geography"));
        if (WilayaId is { } w) Crumbs.Add(((await service.ListGeoAsync(GeoLevel.Wilaya, null, true)).FirstOrDefault(x => x.Id == w)?.Name ?? "Wilaya", $"/Admin/Geography?WilayaId={w}"));
        if (DairaId is { } d && WilayaId is not null) Crumbs.Add(((await service.ListGeoAsync(GeoLevel.Daira, WilayaId, true)).FirstOrDefault(x => x.Id == d)?.Name ?? "Daïra", $"/Admin/Geography?WilayaId={WilayaId}&DairaId={d}"));
        if (CommuneId is { } c && DairaId is not null) Crumbs.Add(((await service.ListGeoAsync(GeoLevel.Commune, DairaId, true)).FirstOrDefault(x => x.Id == c)?.Name ?? "Commune", $"/Admin/Geography?WilayaId={WilayaId}&DairaId={DairaId}&CommuneId={c}"));
    }

    public string Child(Guid id) => ListLevel switch
    {
        GeoLevel.Wilaya => $"/Admin/Geography?WilayaId={id}",
        GeoLevel.Daira => $"/Admin/Geography?WilayaId={WilayaId}&DairaId={id}",
        GeoLevel.Commune => $"/Admin/Geography?WilayaId={WilayaId}&DairaId={DairaId}&CommuneId={id}",
        _ => "",
    };

    public async Task<IActionResult> OnPostSaveAsync(Guid? id, string name, string? code, bool active = false)
    {
        try
        {
            var existing = id is null ? null : (await service.ListGeoAsync(null, null, true)).FirstOrDefault(g => g.Id == id);
            await service.SaveGeoAsync(id, new GeoSave { Level = existing?.Level ?? ListLevel, Name = name, Code = code ?? "", ParentId = existing is null ? ParentId : existing.ParentId, Latitude = existing?.Latitude, Longitude = existing?.Longitude, IsActive = id is null || active });
            TempData["Ok"] = "Zone enregistrée.";
        }
        catch (AppException ex) when (ex is ValidationException or ConflictException)
        {
            TempData["Warn"] = string.Join("\n", ex is ValidationException v ? v.Errors : [ex.Message]);
        }

        return Redirect(Request.Path + Request.QueryString);
    }

    public async Task<IActionResult> OnPostImportAsync(IFormFile? file)
    {
        if (file is null || file.Length == 0 || file.Length > 5 * 1024 * 1024) TempData["Warn"] = "Choisissez un fichier CSV (5 Mo max).";
        else
        {
            try
            {
                await using var s = file.OpenReadStream();
                var report = await service.ImportGeographyAsync(s);
                TempData["Ok"] = $"Import géographie : {report.Created} zone(s) créée(s), {report.Existing} déjà existante(s)." + (report.Errors.Count > 0 ? $" {report.Errors.Count} erreur(s)." : "");
                if (report.Errors.Count > 0) TempData["Warn"] = string.Join("\n", report.Errors.Take(10));
            }
            catch (ValidationException ex) { TempData["Warn"] = ex.Message; }
        }

        return Redirect(Request.Path + Request.QueryString);
    }
}
