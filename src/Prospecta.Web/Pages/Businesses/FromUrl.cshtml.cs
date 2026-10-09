using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Businesses;
using Prospecta.Application.Collection;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Businesses;

public class FromUrlModel(UrlInspectionService inspector, BusinessService businesses, UiLookups lookups) : AppPage
{
    [BindProperty(Name = "Url")] public string? PageUrl { get; set; }
    [BindProperty] public string Kind { get; set; } = "page";
    [BindProperty] public BusinessInput Input { get; set; } = new();
    public UrlInspection? Result { get; private set; }
    public List<SelectListItem> Wilayas = [], Dairas = [], Communes = [], Categories = [];

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostInspectAsync()
    {
        try
        {
            Result = await inspector.InspectAsync(PageUrl);
            Kind = Result.Kind;
            var c = Result.Candidate;
            Input = new BusinessInput { Name = c.Name, Phone = c.Phone, Website = Result.Kind == "page" ? c.Website : null, Address = c.Address, Latitude = c.Latitude, Longitude = c.Longitude, Description = c.Description, SourceUrl = PageUrl?.Trim(), GoogleMapsUrl = Result.Kind == "maps" ? PageUrl?.Trim() : null };
        }
        catch (Application.Common.AppException ex) when (ex is Application.Common.ValidationException or Application.Common.ConflictException or Application.Common.ForbiddenException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (ConnectorException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message + " Vous pouvez saisir la fiche à la main : « Nouvelle entreprise ».");
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        SaveResult? saved = null;
        if (await TryAsync(async () => saved = await businesses.CreateWithSourceAsync(Input, FieldOrigin.External,
                new BusinessService.SourceInfo(Kind == "maps" ? SourceType.PublicUrl : SourceType.PublicWebsite, Kind == "maps" ? "map-link" : "website", null, Input.SourceUrl))))
        {
            TempData[saved!.DuplicateWarnings.Count > 0 ? "Warn" : "Ok"] = saved.DuplicateWarnings.Count > 0 ? string.Join("\n", saved.DuplicateWarnings) : "Entreprise créée depuis l'URL (à vérifier).";
            return Redirect($"/Businesses/Details/{saved.Business.Id}");
        }

        await LoadAsync();
        Result = new UrlInspection(Kind, new Candidate(), []);
        return Page();
    }

    private async Task LoadAsync()
    {
        Wilayas = await lookups.GeoAsync(GeoLevel.Wilaya, null, Input.WilayaId, "Non indiquée");
        Dairas = Input.WilayaId is null ? UiLookups.WithBlank([], "Non indiquée") : await lookups.GeoAsync(GeoLevel.Daira, Input.WilayaId, Input.DairaId, "Non indiquée");
        Communes = Input.DairaId is null ? UiLookups.WithBlank([], "Non indiquée") : await lookups.GeoAsync(GeoLevel.Commune, Input.DairaId, Input.CommuneId, "Non indiquée");
        Categories = await lookups.CategoriesAsync(null, Input.CategoryId, "Non indiquée");
    }
}
