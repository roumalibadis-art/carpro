using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Common;
using Prospecta.Application.Reference;

namespace Prospecta.Web.Pages.Admin;

public class CategoriesModel(ReferenceService service) : AppPage
{
    public IReadOnlyList<CategoryDto> All { get; private set; } = [];

    public async Task OnGetAsync() => All = await service.ListCategoriesAsync(null, false, true);

    public async Task<IActionResult> OnPostSaveAsync(Guid? id, string name, Guid? parentId, bool active = false)
    {
        try
        {
            await service.SaveCategoryAsync(id, name, parentId, id is null || active);
            TempData["Ok"] = "Activité enregistrée.";
        }
        catch (AppException ex) when (ex is ValidationException or ConflictException)
        {
            TempData["Warn"] = ex.Message;
        }

        return RedirectToPage();
    }
}
