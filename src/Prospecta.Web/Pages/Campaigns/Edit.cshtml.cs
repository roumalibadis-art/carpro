using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Prospecting;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Campaigns;

public class EditModel(CampaignService service, UiLookups lookups) : AppPage
{
    [BindProperty(SupportsGet = true)] public Guid? Id { get; set; }
    [BindProperty] public CampaignInput Input { get; set; } = new() { StartDate = DateOnly.FromDateTime(DateTime.Today), EndDate = DateOnly.FromDateTime(DateTime.Today.AddDays(30)) };
    public List<SelectListItem> Wilayas = [], Dairas = [], Communes = [], Categories = [], Managers = [], Team = [];

    public async Task OnGetAsync()
    {
        if (Id is { } id) Input = (await service.GetAsync(id)).Data;
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Guid saved = Guid.Empty;
        if (await TryAsync(async () => saved = await service.SaveAsync(Id, Input)))
        {
            TempData["Ok"] = "Campagne enregistrée.";
            return Redirect($"/Campaigns/Details/{saved}");
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Wilayas = await lookups.GeoAsync(GeoLevel.Wilaya, null, Input.WilayaId, "Toutes");
        Dairas = Input.WilayaId is null ? UiLookups.WithBlank([], "Toutes") : await lookups.GeoAsync(GeoLevel.Daira, Input.WilayaId, Input.DairaId, "Toutes");
        Communes = Input.DairaId is null ? UiLookups.WithBlank([], "Toutes") : await lookups.GeoAsync(GeoLevel.Commune, Input.DairaId, Input.CommuneId, "Toutes");
        Categories = await lookups.CategoriesAsync(null, Input.CategoryId, "Tous secteurs");
        Managers = await lookups.UsersAsync(Input.ManagerUserId, "— moi —");
        Team = (await lookups.UsersAsync(null, "")).Skip(1).Select(u => new SelectListItem(u.Text, u.Value, Input.ParticipantIds.Contains(Guid.Parse(u.Value)))).ToList();
    }
}
