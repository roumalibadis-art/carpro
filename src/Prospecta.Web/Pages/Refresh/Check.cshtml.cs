using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Collection;
using Prospecta.Application.Common;

namespace Prospecta.Web.Pages.Refresh;

public class CheckModel(RefreshService service) : AppPage
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    public RefreshCheck? Data { get; private set; }

    public async Task OnGetAsync()
    {
        try { Data = await service.CheckAsync(Id); }
        catch (AppException ex) when (ex is ValidationException or ConflictException) { ModelState.AddModelError(string.Empty, ex.Message); }
    }

    public async Task<IActionResult> OnPostApplyAsync(string[] fields)
    {
        try
        {
            var (applied, blocked) = await service.ApplyAsync(Id, fields);
            TempData[blocked.Count > 0 ? "Warn" : "Ok"] = $"{applied} champ(s) mis à jour (origine : source externe)." + (blocked.Count > 0 ? $" Non modifiés car confirmés : {string.Join(", ", blocked)}." : "");
        }
        catch (AppException ex) when (ex is ValidationException or ConflictException) { TempData["Warn"] = ex.Message; }
        return Redirect($"/Businesses/Details/{Id}");
    }
}
