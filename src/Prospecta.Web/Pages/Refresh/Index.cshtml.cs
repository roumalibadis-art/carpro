using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Businesses;
using Prospecta.Application.Common;
using Prospecta.Application.Prospecting;

namespace Prospecta.Web.Pages.Refresh;

public class IndexModel(BusinessService service, TimeProvider clock) : AppPage
{
    [BindProperty(SupportsGet = true)] public int Months { get; set; } = 6;
    [BindProperty(SupportsGet = true)] public bool MissingPhone { get; set; }
    [BindProperty(SupportsGet = true)] public bool ContactError { get; set; }
    [BindProperty(SupportsGet = true)] public bool ChangeReported { get; set; }
    [BindProperty(Name = "Page", SupportsGet = true)] public int PageNo { get; set; } = 1;
    public PagedResult<BusinessListItem> Result { get; private set; } = new([], 0, 1, 25);

    public async Task OnGetAsync()
    {
        var f = new BusinessFilter { Page = PageNo, PageSize = 25, SortBy = "verified", HasWebsite = true, StaleBefore = Months > 0 ? clock.GetUtcNow().UtcDateTime.AddMonths(-Math.Clamp(Months, 1, 60)) : null };
        if (MissingPhone) f.HasPhone = false;
        if (ContactError) f.HasContactError = true;
        if (ChangeReported) f.ChangeReported = true;
        Result = await service.SearchAsync(f);
    }
}
