using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Prospecting;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Map;

public class IndexModel(UiLookups lookups, CampaignService campaigns) : AppPage
{
    public List<SelectListItem> Wilayas = [], Categories = [], Statuses = [], Campaigns = [], Users = [];

    public async Task OnGetAsync()
    {
        Wilayas = await lookups.GeoAsync(GeoLevel.Wilaya, null, null, "Toutes");
        Categories = await lookups.CategoriesAsync(null, null, "Toutes");
        Statuses = await lookups.StatusesAsync(StatusKind.Census, null, "Tous");
        Campaigns = Can(Prospecta.Application.Security.Permissions.ActivityRecord) ? await lookups.CampaignsAsync(campaigns, null, "Toutes") : UiLookups.WithBlank([], "Toutes");
        Users = Can(Prospecta.Application.Security.Permissions.BusinessViewAll) ? await lookups.UsersAsync(null, "Tous") : [];
    }
}
