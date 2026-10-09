using Prospecta.Application.Common;
using Prospecta.Application.Dashboard;
using Microsoft.AspNetCore.Mvc;

namespace Prospecta.Web.Pages.Admin;

public class AuditModel(AuditQueryService service) : AppPage
{
    [BindProperty(SupportsGet = true)] public string? Action { get; set; }
    [BindProperty(SupportsGet = true)] public string? UserName { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
    [BindProperty(Name = "Page", SupportsGet = true)] public int PageNo { get; set; } = 1;
    public PagedResult<AuditDto> Result { get; private set; } = new([], 0, 1, 50);

    public async Task OnGetAsync() => Result = await service.ListAsync(Action, UserName, From, To, PageNo, 50);
}
