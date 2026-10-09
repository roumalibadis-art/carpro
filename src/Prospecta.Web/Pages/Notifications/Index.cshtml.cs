using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Common;
using Prospecta.Application.Prospecting;

namespace Prospecta.Web.Pages.Notifications;

public class IndexModel(NotificationService service) : AppPage
{
    [BindProperty(Name = "Page", SupportsGet = true)] public int PageNo { get; set; } = 1;
    public PagedResult<NotificationDto> Result { get; private set; } = new([], 0, 1, 25);

    public async Task OnGetAsync() => Result = await service.ListAsync(PageNo, 25);

    public async Task<IActionResult> OnPostReadAllAsync()
    {
        await service.MarkReadAsync(null);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostReadAsync(Guid id, string? link)
    {
        await service.MarkReadAsync(id);
        return Url.IsLocalUrl(link) ? LocalRedirect(link!) : RedirectToPage();
    }
}
