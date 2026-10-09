using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Businesses;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Businesses;

public class EditModel(BusinessService service, UiLookups lookups) : AppPage
{
    [BindProperty(SupportsGet = true)] public Guid? Id { get; set; }
    [BindProperty] public BusinessInput Input { get; set; } = new();
    public Dictionary<string, FieldOrigin> Origins { get; private set; } = [];
    public List<SelectListItem> Wilayas = [], Dairas = [], Communes = [], Districts = [], Categories = [], SubCategories = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is { } id)
        {
            var d = await service.GetAsync(id);
            if (!d.CanEdit) return Forbid();
            Input = d.Data;
            Origins = d.Origins.ToDictionary(o => o.Key, o => o.Value);
        }

        await LoadListsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SaveResult? result = null;
        var ok = await TryAsync(async () => result = Id is { } id ? await service.UpdateAsync(id, Input) : await service.CreateAsync(Input));
        if (!ok)
        {
            await LoadListsAsync();
            return Page();
        }

        var notes = result!.BlockedFields.Concat(result.DuplicateWarnings).ToList();
        if (notes.Count > 0) TempData["Warn"] = string.Join("\n", notes);
        else TempData["Ok"] = Id is null ? "Entreprise créée." : "Fiche enregistrée.";
        return Redirect($"/Businesses/Details/{result.Business.Id}");
    }

    private async Task LoadListsAsync()
    {
        Wilayas = await lookups.GeoAsync(GeoLevel.Wilaya, null, Input.WilayaId, "Non indiquée");
        Dairas = Input.WilayaId is null ? UiLookups.WithBlank([], "Non indiquée") : await lookups.GeoAsync(GeoLevel.Daira, Input.WilayaId, Input.DairaId, "Non indiquée");
        Communes = Input.DairaId is null ? UiLookups.WithBlank([], "Non indiquée") : await lookups.GeoAsync(GeoLevel.Commune, Input.DairaId, Input.CommuneId, "Non indiquée");
        Districts = Input.CommuneId is null ? UiLookups.WithBlank([], "Non indiqué") : await lookups.GeoAsync(GeoLevel.Quartier, Input.CommuneId, Input.DistrictId, "Non indiqué");
        Categories = await lookups.CategoriesAsync(null, Input.CategoryId, "Non indiquée");
        SubCategories = Input.CategoryId is null ? UiLookups.WithBlank([], "Non indiquée") : await lookups.CategoriesAsync(Input.CategoryId, Input.SubCategoryId, "Non indiquée");
    }
}
