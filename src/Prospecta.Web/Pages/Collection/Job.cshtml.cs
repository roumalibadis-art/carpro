using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Collection;
using Prospecta.Application.Common;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Collection;

public class JobModel(CollectionService service) : AppPage
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty(SupportsGet = true)] public CollectionResultStatus? Status { get; set; }
    [BindProperty(Name = "Page", SupportsGet = true)] public int PageNo { get; set; } = 1;
    public JobDto Job { get; private set; } = default!;
    public PagedResult<ResultDto> Results { get; private set; } = new([], 0, 1, 50);

    public async Task OnGetAsync() => (Job, Results) = await service.GetAsync(Id, Status, PageNo, 50);

    private async Task<IActionResult> Act(Func<Task<string>> action)
    {
        try { TempData["Ok"] = await action(); }
        catch (AppException ex) when (ex is ValidationException or ConflictException or ForbiddenException) { TempData["Warn"] = ex.Message; }
        return Redirect($"/Collection/Job/{Id}");
    }

    public Task<IActionResult> OnPostImportAsync(Guid[] ids, bool includeDuplicates, bool all) => Act(async () =>
    {
        var (n, skipped) = await service.ImportAsync(Id, all ? null : ids, includeDuplicates);
        return $"{n} fiche(s) créée(s) (statut « à vérifier »), {skipped} ignorée(s).";
    });

    public Task<IActionResult> OnPostRejectAsync(Guid[] ids) => Act(async () => { await service.RejectAsync(Id, ids); return "Résultats écartés."; });

    public async Task<IActionResult> OnPostRetryAsync()
    {
        try
        {
            var j = await service.RetryAsync(Id);
            TempData[j.Status == CollectionJobStatus.Completed ? "Ok" : "Warn"] = j.Message ?? j.Status.ToString();
            return Redirect($"/Collection/Job/{j.Id}");
        }
        catch (AppException ex) when (ex is ValidationException or ConflictException or ForbiddenException) { TempData["Warn"] = ex.Message; return Redirect($"/Collection/Job/{Id}"); }
    }
}
