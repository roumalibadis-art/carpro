using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Common;
using Prospecta.Application.Prospecting;

namespace Prospecta.Web.Pages.Outings;

public class IndexModel(OutingService service) : AppPage
{
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(Name = "Page", SupportsGet = true)] public int PageNo { get; set; } = 1;
    public PagedResult<OutingListItem> Result { get; private set; } = new([], 0, 1, 25);

    public async Task OnGetAsync() => Result = await service.ListAsync(From, To, null, PageNo, 25);
}
