using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Common;
using Prospecta.Application.Duplicates;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Duplicates;

public class IndexModel(DuplicateService service) : AppPage
{
    [BindProperty(Name = "Page", SupportsGet = true)] public int PageNo { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public DuplicateStatus Status { get; set; } = DuplicateStatus.Pending;
    public PagedResult<DuplicateDto> Result { get; private set; } = new([], 0, 1, 25);

    public async Task OnGetAsync() => Result = await service.ListAsync(Status, PageNo, 25);

    public async Task<IActionResult> OnPostRescanAsync()
    {
        var pairs = await service.RescanAsync(500);
        TempData["Ok"] = $"Analyse terminée : {pairs} couple(s) examiné(s) ou mis à jour.";
        return RedirectToPage();
    }
}
