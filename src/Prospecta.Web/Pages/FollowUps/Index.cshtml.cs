using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Common;
using Prospecta.Application.Prospecting;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.FollowUps;

public class IndexModel(FollowUpService service, UiLookups lookups) : AppPage
{
    [BindProperty(SupportsGet = true)] public string? When { get; set; }
    [BindProperty(SupportsGet = true)] public FollowUpStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? UserId { get; set; }
    [BindProperty(Name = "Page", SupportsGet = true)] public int PageNo { get; set; } = 1;
    public PagedResult<FollowUpDto> Result { get; private set; } = new([], 0, 1, 25);
    public List<SelectListItem> Users { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var status = Status ?? (When is null ? FollowUpStatus.ToDo : null);
        Result = await service.SearchAsync(new FollowUpFilter { When = When, Status = status, UserId = UserId, Page = PageNo });
        Users = Can(Prospecta.Application.Security.Permissions.ActivityViewAll) ? await lookups.UsersAsync(UserId, "Tous") : [];
    }

    private async Task<IActionResult> Act(Func<Task> action, string ok)
    {
        try { await action(); TempData["Ok"] = ok; }
        catch (AppException ex) when (ex is ValidationException or ConflictException) { TempData["Warn"] = ex.Message; }
        return Redirect("/FollowUps" + Request.QueryString);
    }

    public Task<IActionResult> OnPostCompleteAsync(Guid id, string? result, DateOnly? nextDue) => Act(() => service.CompleteAsync(id, result, nextDue), "Relance effectuée.");
    public Task<IActionResult> OnPostPostponeAsync(Guid id, DateOnly newDue) => Act(() => service.PostponeAsync(id, newDue), "Relance reportée.");
    public Task<IActionResult> OnPostCancelAsync(Guid id, string? reason) => Act(() => service.CancelAsync(id, reason), "Relance annulée.");
}
