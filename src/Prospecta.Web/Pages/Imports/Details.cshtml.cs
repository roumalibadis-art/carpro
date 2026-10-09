using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Common;
using Prospecta.Application.Imports;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Imports;

public class DetailsModel(ImportService service) : AppPage
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty(SupportsGet = true)] public ImportRowStatus? Status { get; set; }
    [BindProperty(Name = "Page", SupportsGet = true)] public int PageNo { get; set; } = 1;
    public ImportBatchDto Batch { get; private set; } = default!;
    public PagedResult<ImportRowDto> Rows { get; private set; } = new([], 0, 1, 50);

    public async Task OnGetAsync()
    {
        Batch = await service.GetAsync(Id);
        Rows = await service.RowsAsync(Id, Status, PageNo, 50);
    }

    public async Task<IActionResult> OnPostMapAsync()
    {
        var mapping = ImportService.Fields.Keys.Where(k => Request.Form.ContainsKey("map_" + k) && !string.IsNullOrEmpty(Request.Form["map_" + k]))
            .ToDictionary(k => k, k => (string)Request.Form["map_" + k]!);
        return await Run(() => service.MapAsync(Id, mapping), "Colonnes associées : vérifiez l'aperçu ci-dessous.");
    }

    public Task<IActionResult> OnPostCommitAsync(bool includeDuplicates) => Run(() => service.CommitAsync(Id, includeDuplicates), "Import terminé : consultez le compte rendu.");

    public async Task<IActionResult> OnPostCancelAsync()
    {
        await service.CancelAsync(Id);
        TempData["Ok"] = "Import annulé.";
        return Redirect("/Imports");
    }

    private async Task<IActionResult> Run(Func<Task> action, string ok)
    {
        try
        {
            await action();
            TempData["Ok"] = ok;
        }
        catch (AppException ex) when (ex is ValidationException or ConflictException)
        {
            TempData["Warn"] = string.Join("\n", ex is ValidationException v ? v.Errors : [ex.Message]);
        }

        return Redirect($"/Imports/Details/{Id}");
    }
}
