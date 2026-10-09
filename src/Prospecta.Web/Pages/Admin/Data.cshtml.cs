using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Businesses;
using Prospecta.Application.Common;

namespace Prospecta.Web.Pages.Admin;

public class DataModel(DataRetentionService service) : AppPage
{
    [BindProperty(Name = "Page", SupportsGet = true)] public int PageNo { get; set; } = 1;
    public PagedResult<DeletedBusinessDto> Result { get; private set; } = new([], 0, 1, 25);

    public async Task OnGetAsync() => Result = await service.ListDeletedAsync(PageNo, 25);

    public async Task<IActionResult> OnPostPurgeAsync(Guid id)
    {
        try { await service.PurgeAsync(id); TempData["Ok"] = "Entreprise purgée définitivement (avec ses visites, relances et historique)."; }
        catch (AppException ex) when (ex is ConflictException or NotFoundException) { TempData["Warn"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPurgeOldAsync(int days)
    {
        try { TempData["Ok"] = $"{await service.PurgeOlderThanAsync(days)} entreprise(s) purgée(s)."; }
        catch (ValidationException ex) { TempData["Warn"] = ex.Message; }
        return RedirectToPage();
    }
}
