using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Businesses;
using Prospecta.Application.Prospecting;
using Prospecta.Application.Reporting;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Reports;

public class NewModel(ReportService service, OutingService outings, CampaignService campaigns, UiLookups lookups, TimeProvider clock) : AppPage
{
    [BindProperty(SupportsGet = true)] public ReportType Type { get; set; } = ReportType.MarketStudy;
    [BindProperty] public ReportParameters Params { get; set; } = new();
    [BindProperty] public BusinessFilter Filter { get; set; } = new();
    [BindProperty] public bool Share { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    public Application.Reporting.ReportDocument? Preview { get; private set; }
    public List<SelectListItem> Outings = [], Campaigns = [], Users = [], Categories = [], Wilayas = [], Dairas = [], Communes = [];

    public async Task OnGetAsync()
    {
        var today = Dates.Today(clock);
        Params.From = From ?? new DateOnly(today.Year, today.Month, 1);
        Params.To = To ?? today;
        await LoadAsync();
    }

    private ReportParameters Build() { Params.Filter = Filter; return Params; }

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        await TryAsync(async () => Preview = await service.PreviewAsync(Type, Build()));
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        ReportSummary? saved = null;
        if (await TryAsync(async () => saved = await service.SaveAsync(Type, Build(), Share)))
        {
            TempData["Ok"] = "Rapport enregistré : ses chiffres sont figés à cet instant.";
            return Redirect($"/Reports/View/{saved!.Id}");
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Campaigns = await lookups.CampaignsAsync(campaigns, Params.CampaignId, "Toutes");
        Users = await lookups.UsersAsync(Params.UserId, "Moi");
        Categories = await lookups.CategoriesAsync(null, Params.CategoryId, "Tous");
        Wilayas = await lookups.GeoAsync(GeoLevel.Wilaya, null, Filter.WilayaId, "Toutes");
        Dairas = Filter.WilayaId is null ? UiLookups.WithBlank([], "Toutes") : await lookups.GeoAsync(GeoLevel.Daira, Filter.WilayaId, Filter.DairaId, "Toutes");
        Communes = Filter.DairaId is null ? UiLookups.WithBlank([], "Toutes") : await lookups.GeoAsync(GeoLevel.Commune, Filter.DairaId, Filter.CommuneId, "Toutes");
        try
        {
            Outings = UiLookups.WithBlank((await outings.ListAsync(null, null, null, 1, 100)).Items.Select(o => new SelectListItem($"{o.Date:dd/MM/yyyy} — {o.Zone ?? "sortie"} ({o.Status})", o.Id.ToString(), o.Id == Params.OutingId)), "— choisir —");
        }
        catch (Prospecta.Application.Common.ForbiddenException) { Outings = UiLookups.WithBlank([], "— aucune sortie accessible —"); }
    }
}
