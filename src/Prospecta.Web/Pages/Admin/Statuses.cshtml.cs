using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Common;
using Prospecta.Application.Reference;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Admin;

public class StatusesModel(ReferenceService service) : AppPage
{
    public IReadOnlyList<StatusDto> All { get; private set; } = [];

    public async Task OnGetAsync() => All = await service.ListStatusesAsync(null, true);

    public async Task<IActionResult> OnPostSaveAsync(Guid? id, StatusKind kind, string label, int sortOrder, bool active = false)
    {
        try
        {
            await service.SaveStatusAsync(id, kind, "", label, sortOrder, id is null || active);
            TempData["Ok"] = "Statut enregistré.";
        }
        catch (AppException ex) when (ex is ValidationException or ConflictException)
        {
            TempData["Warn"] = ex.Message;
        }

        return RedirectToPage();
    }
}
