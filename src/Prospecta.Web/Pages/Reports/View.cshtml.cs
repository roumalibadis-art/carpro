using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Common;
using Prospecta.Application.Reporting;

namespace Prospecta.Web.Pages.Reports;

public class ViewModel(ReportService service) : AppPage
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    public ReportView Data { get; private set; } = default!;

    public async Task OnGetAsync() => Data = await service.GetAsync(Id);

    public async Task<IActionResult> OnGetExportAsync(string format)
    {
        var file = await service.ExportAsync(Id, format);
        return File(file.Content, file.ContentType, file.FileName);
    }

    private async Task<IActionResult> Act(Func<Task> action, string ok, string? to = null)
    {
        try { await action(); TempData["Ok"] = ok; }
        catch (AppException ex) when (ex is ValidationException or ConflictException or ForbiddenException) { TempData["Warn"] = ex.Message; }
        return Redirect(to ?? $"/Reports/View/{Id}");
    }

    public Task<IActionResult> OnPostShareAsync(bool shared) => Act(() => service.SetSharedAsync(Id, shared), shared ? "Rapport partagé avec votre hiérarchie." : "Rapport repassé en privé.");
    public Task<IActionResult> OnPostValidateAsync(string? note) => Act(() => service.ValidateAsync(Id, note), "Rapport validé.");
    public Task<IActionResult> OnPostDeleteAsync() => Act(() => service.DeleteAsync(Id), "Rapport supprimé.", "/Reports");

    public async Task<IActionResult> OnPostDuplicateAsync()
    {
        try
        {
            var copy = await service.DuplicateAsync(Id, null);
            TempData["Ok"] = "Nouveau rapport généré avec les mêmes paramètres et des chiffres à jour.";
            return Redirect($"/Reports/View/{copy.Id}");
        }
        catch (AppException ex) when (ex is ValidationException or ConflictException or ForbiddenException)
        {
            TempData["Warn"] = ex.Message;
            return Redirect($"/Reports/View/{Id}");
        }
    }
}
