using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Common;
using Prospecta.Application.Imports;

namespace Prospecta.Web.Pages.Imports;

[RequestSizeLimit(6 * 1024 * 1024)]
public class IndexModel(ImportService service, Prospecta.Application.Collection.TemplateService templates) : AppPage
{
    public IReadOnlyList<ImportBatchDto> Batches { get; private set; } = [];

    public async Task OnGetAsync() => Batches = await service.ListAsync();

    public async Task<IActionResult> OnGetTemplateAsync(string format = "xlsx")
    {
        var f = await templates.BusinessTemplateAsync(format);
        return File(f.Content, f.ContentType, f.FileName);
    }

    public async Task<IActionResult> OnPostAsync(IFormFile? file)
    {
        if (file is null) ModelState.AddModelError(string.Empty, "Choisissez un fichier .csv ou .xlsx.");
        else
        {
            ImportBatchDto? batch = null;
            await using var s = file.OpenReadStream();
            if (await TryAsync(async () => batch = await service.UploadAsync(s, file.FileName))) return Redirect($"/Imports/Details/{batch!.Id}");
        }

        Batches = await service.ListAsync();
        return Page();
    }
}
