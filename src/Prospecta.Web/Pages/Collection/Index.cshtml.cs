using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Collection;
using Prospecta.Application.Common;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Collection;

public class IndexModel(CollectionService service, UiLookups lookups) : AppPage
{
    [BindProperty] public CollectionRequest Request2 { get; set; } = new();
    [BindProperty] public Guid? WilayaId { get; set; }
    [BindProperty] public Guid? DairaId { get; set; }
    [BindProperty] public Guid? SubCategoryId { get; set; }
    [BindProperty(Name = "Page", SupportsGet = true)] public int PageNo { get; set; } = 1;
    public PagedResult<JobDto> Jobs { get; private set; } = new([], 0, 1, 25);
    public List<SelectListItem> Wilayas = [], Dairas = [], Communes = [], Categories = [], SubCategories = [];

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        // The most precise area chosen wins: commune, then daïra, then wilaya.
        Request2.AreaId = Request2.AreaId != Guid.Empty ? Request2.AreaId : DairaId ?? WilayaId ?? Guid.Empty;
        if (SubCategoryId is { } sc) Request2.CategoryId = sc; // the sub-activity's OpenStreetMap filter (or its parent's) drives the search
        JobDto? job = null;
        if (await TryAsync(async () => job = await service.SearchAsync(Request2)))
        {
            TempData[job!.Status == CollectionJobStatus.Completed ? "Ok" : "Warn"] = job.Message ?? job.Status.ToString();
            return Redirect($"/Collection/Job/{job.Id}");
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Jobs = await service.ListJobsAsync(PageNo, 10);
        Wilayas = await lookups.GeoAsync(GeoLevel.Wilaya, null, WilayaId, "—");
        Dairas = WilayaId is null ? UiLookups.WithBlank([], "—") : await lookups.GeoAsync(GeoLevel.Daira, WilayaId, DairaId, "—");
        Communes = DairaId is null ? UiLookups.WithBlank([], "—") : await lookups.GeoAsync(GeoLevel.Commune, DairaId, Request2.AreaId, "Toute la daïra");
        Categories = await lookups.CategoriesAsync(null, null, "—");
        SubCategories = [];
    }
}
