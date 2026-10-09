using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Common;
using Prospecta.Application.Prospecting;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Visits;

public class IndexModel(VisitService service, UiLookups lookups) : AppPage
{
    [BindProperty(SupportsGet = true)] public VisitFilter Filter { get; set; } = new();
    public PagedResult<VisitDto> Result { get; private set; } = new([], 0, 1, 25);
    public List<SelectListItem> Users { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Result = await service.SearchAsync(Filter);
        Users = Can(Prospecta.Application.Security.Permissions.ActivityViewAll) ? await lookups.UsersAsync(Filter.UserId, "Tous") : [];
    }

    private async Task<IActionResult> Act(Func<Task> action, string ok)
    {
        try { await action(); TempData["Ok"] = ok; }
        catch (AppException ex) when (ex is ValidationException or ConflictException) { TempData["Warn"] = ex.Message; }
        return Redirect("/Visits" + Request.QueryString);
    }

    public Task<IActionResult> OnPostCancelAsync(Guid id, string? reason) => Act(() => service.CancelAsync(id, reason), "Action annulée.");
    public Task<IActionResult> OnPostPostponeAsync(Guid id, DateTime newDate) => Act(async () => await service.PostponeAsync(id, Dates.LocalToUtc(newDate)), "Action reportée : une nouvelle action est planifiée.");
}
