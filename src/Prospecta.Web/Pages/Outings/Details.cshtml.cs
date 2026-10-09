using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Common;
using Prospecta.Application.Prospecting;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Outings;

public class DetailsModel(OutingService service, VisitService visits) : AppPage
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    public OutingDetail Data { get; private set; } = default!;
    public IReadOnlyList<VisitDto> Visits { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Data = await service.GetAsync(Id);
        Visits = (await visits.SearchAsync(new VisitFilter { OutingId = Id, PageSize = 100 })).Items;
    }

    private async Task<IActionResult> Act(Func<Task> action, string ok)
    {
        try { await action(); TempData["Ok"] = ok; }
        catch (AppException ex) when (ex is ValidationException or ConflictException or ForbiddenException) { TempData["Warn"] = string.Join("\n", ex is ValidationException v ? v.Errors : [ex.Message]); }
        return Redirect($"/Outings/Details/{Id}");
    }

    public Task<IActionResult> OnPostCloseAsync(OutingStatus status, string? observations) => Act(() => service.CloseAsync(Id, status, observations), "Sortie clôturée.");

    public Task<IActionResult> OnPostExpenseAsync(ExpenseKind kind, ExpenseCategory category, decimal amount, DateOnly date, string? description, string? receipt) =>
        Act(async () => await service.AddExpenseAsync(new ExpenseInput { OutingId = Id, Kind = kind, Category = category, Amount = amount, Date = date, Description = description, ReceiptReference = receipt }), "Dépense enregistrée.");

    public Task<IActionResult> OnPostDeleteExpenseAsync(Guid expenseId) => Act(() => service.DeleteExpenseAsync(expenseId), "Dépense supprimée.");
}
