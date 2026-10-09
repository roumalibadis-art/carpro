using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Prospecting;
using Prospecta.Application.Reporting;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Evaluation;

public class IndexModel(IndicatorService indicators, CampaignService campaigns, UiLookups lookups, TimeProvider clock) : AppPage
{
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? CampaignId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? UserId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? CommuneId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? CategoryId { get; set; }
    public IndicatorSet? Data { get; private set; }
    public List<SelectListItem> Campaigns = [], Users = [], Categories = [];

    public async Task OnGetAsync()
    {
        var today = Dates.Today(clock);
        From ??= new DateOnly(today.Year, today.Month, 1);
        To ??= today;
        Campaigns = await lookups.CampaignsAsync(campaigns, CampaignId, "Toutes");
        Users = await lookups.UsersAsync(UserId, "Tous (selon mes droits)");
        Categories = await lookups.CategoriesAsync(null, CategoryId, "Tous");
        if (await TryAsync(async () => Data = await indicators.ComputeAsync(new IndicatorFilter { From = From.Value, To = To.Value, CampaignId = CampaignId, UserId = UserId, CommuneId = CommuneId, CategoryId = CategoryId })))
            return;
    }
}
