using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Prospecting;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Visits;

public class CompleteModel(VisitService visits, UiLookups lookups) : AppPage
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty] public VisitResult Result { get; set; } = new();
    public VisitDto Visit { get; private set; } = default!;
    public List<SelectListItem> Outcomes { get; private set; } = [];

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        if (await TryAsync(async () => await visits.CompleteAsync(Id, Result)))
        {
            TempData["Ok"] = "Résultat enregistré.";
            return Redirect("/Visits");
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Visit = await visits.GetAsync(Id);
        Outcomes = await lookups.StatusesAsync(StatusKind.Outcome, Result.OutcomeStatusId, "— inchangé —");
    }
}
