using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Businesses;
using Prospecta.Application.Dashboard;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages;

public class IndexModel(DashboardService dashboard, UiLookups lookups) : AppPage
{
    [BindProperty(SupportsGet = true)] public BusinessFilter Filter { get; set; } = new();
    public DashboardDto Data { get; private set; } = default!;
    public List<SelectListItem> Wilayas { get; private set; } = [];
    public List<SelectListItem> Communes { get; private set; } = [];
    public List<SelectListItem> Categories { get; private set; } = [];
    public List<SelectListItem> Users { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Filter.Page = 1;
        Data = await dashboard.GetAsync(Filter, HttpContext.RequestAborted);
        Wilayas = await lookups.GeoAsync(GeoLevel.Wilaya, null, Filter.WilayaId, "Toutes les wilayas");
        Communes = Filter.DairaId is not null
            ? await lookups.GeoAsync(GeoLevel.Commune, Filter.DairaId, Filter.CommuneId, "Toutes les communes")
            : UiLookups.WithBlank([], "Toutes les communes");
        Categories = await lookups.CategoriesAsync(null, Filter.CategoryId, "Tous les secteurs");
        Users = Can(Prospecta.Application.Security.Permissions.BusinessViewAll) ? await lookups.UsersAsync(Filter.ResponsibleUserId, "Tous les utilisateurs") : [];
    }
}
