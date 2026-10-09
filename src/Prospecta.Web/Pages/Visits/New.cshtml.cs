using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Businesses;
using Prospecta.Application.Prospecting;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Visits;

public class NewModel(VisitService visits, BusinessService businesses, CampaignService campaigns, OutingService outings, UiLookups lookups) : AppPage
{
    [BindProperty(SupportsGet = true)] public Guid BusinessId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? CampaignId { get; set; }
    [BindProperty] public DateTime When { get; set; } = DateTime.Now.AddDays(1).Date.AddHours(9);
    [BindProperty] public VisitAction Action { get; set; } = VisitAction.Visit;
    [BindProperty] public Guid? OutingId { get; set; }
    [BindProperty] public Guid? UserId { get; set; }
    [BindProperty] public string? Comment { get; set; }
    [BindProperty] public bool AlreadyDone { get; set; }
    [BindProperty] public VisitResult Result { get; set; } = new();
    public string BusinessName { get; private set; } = string.Empty;
    public List<SelectListItem> Campaigns = [], Outings = [], Users = [], Outcomes = [];

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        return BusinessId == Guid.Empty ? Redirect("/Businesses") : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var input = new VisitInput { BusinessId = BusinessId, UserId = UserId, ScheduledAt = Dates.LocalToUtc(When), Action = Action, CampaignId = CampaignId, OutingId = OutingId, Comment = Comment };
        var ok = await TryAsync(async () =>
        {
            if (AlreadyDone) await visits.LogDoneAsync(input, Result);
            else await visits.PlanAsync(input);
        });
        if (ok)
        {
            TempData["Ok"] = AlreadyDone ? "Action enregistrée." : "Action planifiée.";
            return Redirect($"/Businesses/Details/{BusinessId}");
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        BusinessName = (await businesses.GetAsync(BusinessId)).Data.Name;
        Campaigns = await lookups.CampaignsAsync(campaigns, CampaignId, "— aucune —");
        try
        {
            Outings = UiLookups.WithBlank((await outings.ListAsync(DateOnly.FromDateTime(DateTime.Today.AddDays(-7)), null, null, 1, 50)).Items.Where(o => o.Status == OutingStatus.Planned)
                .Select(o => new SelectListItem($"{o.Date:dd/MM/yyyy} — {o.Zone ?? "sortie"}", o.Id.ToString(), o.Id == OutingId)), "— aucune —");
        }
        catch (Prospecta.Application.Common.ForbiddenException) { Outings = UiLookups.WithBlank([], "— aucune —"); }
        Users = Can(Prospecta.Application.Security.Permissions.ActivityViewAll) ? await lookups.UsersAsync(UserId, "— moi —") : [];
        Outcomes = await lookups.StatusesAsync(StatusKind.Outcome, Result.OutcomeStatusId, "— inchangé —");
    }
}
