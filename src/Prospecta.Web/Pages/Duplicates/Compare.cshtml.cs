using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Common;
using Prospecta.Application.Duplicates;

namespace Prospecta.Web.Pages.Duplicates;

public class CompareModel(DuplicateService service) : AppPage
{
    public static readonly (string Field, string Label)[] Fields =
    [
        ("Name", "Nom commercial"), ("LegalName", "Nom légal"), ("Description", "Description"), ("Address", "Adresse"), ("Phone", "Téléphone"),
        ("Website", "Site web"), ("GoogleMapsUrl", "URL Google Maps"), ("SourceUrl", "URL source"), ("PlusCode", "Plus Code"), ("Latitude", "Latitude"), ("Longitude", "Longitude"),
    ];

    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    public DuplicateDto Data { get; private set; } = default!;

    public async Task OnGetAsync() => Data = await service.CompareAsync(Id);

    public async Task<IActionResult> OnPostMergeAsync(Guid survivorId, string[]? useOther)
    {
        try
        {
            var id = await service.MergeAsync(Id, survivorId, useOther?.ToHashSet());
            TempData["Ok"] = "Fiches fusionnées : sources, historique et affectations ont été conservés.";
            return Redirect($"/Businesses/Details/{id}");
        }
        catch (AppException ex) when (ex is ValidationException or ConflictException)
        {
            TempData["Warn"] = ex.Message;
            return Redirect($"/Duplicates/Compare/{Id}");
        }
    }

    public async Task<IActionResult> OnPostDismissAsync()
    {
        try
        {
            await service.DismissAsync(Id);
            TempData["Ok"] = "Couple marqué « pas un doublon ».";
        }
        catch (ConflictException ex)
        {
            TempData["Warn"] = ex.Message;
        }

        return Redirect("/Duplicates");
    }
}
