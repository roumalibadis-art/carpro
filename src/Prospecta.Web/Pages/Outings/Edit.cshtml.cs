using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Prospecting;

namespace Prospecta.Web.Pages.Outings;

public class EditModel(OutingService service, CampaignService campaigns, UiLookups lookups) : AppPage
{
    [BindProperty(SupportsGet = true)] public Guid? Id { get; set; }
    [BindProperty] public OutingInput Input { get; set; } = new() { Date = DateOnly.FromDateTime(DateTime.Today.AddDays(1)) };
    public List<SelectListItem> Campaigns = [], Managers = [], Team = [];

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
            TempData["Ok"] = "Sortie enregistrée.";
            return Redirect($"/Outings/Details/{saved}");
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Campaigns = await lookups.CampaignsAsync(campaigns, Input.CampaignId, "— aucune —");
        Managers = await lookups.UsersAsync(Input.ManagerUserId, "— moi —");
        Team = (await lookups.UsersAsync(null, "")).Skip(1).Select(u => new SelectListItem(u.Text, u.Value, Input.ParticipantIds.Contains(Guid.Parse(u.Value)))).ToList();
    }
}
