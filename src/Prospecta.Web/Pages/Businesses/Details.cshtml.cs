using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Businesses;
using Prospecta.Application.Common;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Businesses;

public class DetailsModel(BusinessService service, UiLookups lookups, Prospecta.Application.Prospecting.VisitService visitService, Prospecta.Application.Prospecting.FollowUpService followUpService) : AppPage
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty(SupportsGet = true)] public int HistoryPage { get; set; } = 1;
    public BusinessDetail Data { get; private set; } = default!;
    public PagedResult<HistoryDto> History { get; private set; } = new([], 0, 1, 20);
    public IReadOnlyList<Prospecta.Application.Prospecting.VisitDto> Visits { get; private set; } = [];
    public IReadOnlyList<Prospecta.Application.Prospecting.FollowUpDto> FollowUps { get; private set; } = [];
    public List<SelectListItem> CensusStatuses = [], ProcessingStatuses = [], OutcomeStatuses = [], Users = [];

    private async Task LoadAsync()
    {
        Data = await service.GetAsync(Id);
        History = await service.HistoryAsync(Id, HistoryPage, 20);
        if (Can(Prospecta.Application.Security.Permissions.ActivityRecord))
        {
            Visits = await visitService.HistoryForBusinessAsync(Id);
            FollowUps = (await followUpService.SearchAsync(new Prospecta.Application.Prospecting.FollowUpFilter { BusinessId = Id, PageSize = 20 })).Items;
        }

        CensusStatuses = await lookups.StatusesAsync(StatusKind.Census, Data.CensusStatus.Id, "—");
        ProcessingStatuses = await lookups.StatusesAsync(StatusKind.Processing, Data.ProcessingStatus.Id, "—");
        OutcomeStatuses = await lookups.StatusesAsync(StatusKind.Outcome, Data.OutcomeStatus.Id, "—");
        Users = await lookups.UsersAsync(null, "— choisir —");
    }

    public async Task OnGetAsync() => await LoadAsync();

    private async Task<IActionResult> Act(Func<Task> action, string success)
    {
        try
        {
            await action();
            TempData["Ok"] = success;
        }
        catch (AppException ex) when (ex is ValidationException or ConflictException or ForbiddenException)
        {
            TempData["Warn"] = ex.Message;
        }

        return Redirect($"/Businesses/Details/{Id}");
    }

    public Task<IActionResult> OnPostVerifyAsync() => Act(() => service.VerifyAsync(Id), "Fiche vérifiée : les valeurs connues sont désormais confirmées.");
    public Task<IActionResult> OnPostStatusAsync(StatusKind kind, Guid statusId) => Act(() => service.SetStatusAsync(Id, kind, statusId), "Statut mis à jour.");
    public Task<IActionResult> OnPostFlagsAsync(bool contactError, bool changeReported) => Act(() => service.SetFlagsAsync(Id, contactError, changeReported), "Signalements mis à jour.");
    public Task<IActionResult> OnPostAssignAsync(Guid userId) => Act(() => service.AssignAsync([Id], userId), "Affectation enregistrée.");
    public Task<IActionResult> OnPostUnassignAsync(Guid userId) => Act(() => service.UnassignAsync(Id, userId), "Affectation retirée.");

    public Task<IActionResult> OnPostFollowUpAsync(DateOnly due, string reason, Priority priority) =>
        Act(async () => await followUpService.CreateAsync(new Prospecta.Application.Prospecting.FollowUpInput { BusinessId = Id, DueDate = due, Reason = reason, Priority = priority }), "Relance créée.");

    public async Task<IActionResult> OnPostDeleteAsync(string? reason)
    {
        await service.DeleteAsync(Id, reason);
        TempData["Ok"] = "Entreprise supprimée (suppression logique, historique conservé).";
        return Redirect("/Businesses");
    }

    public string OriginClass(string field) => Data.Origins.TryGetValue(field, out var o) ? "o-" + o : "";

    public string OriginLabel(string field) => Data.Origins.TryGetValue(field, out var o)
        ? o switch { FieldOrigin.Confirmed => "confirmé", FieldOrigin.External => "source externe", FieldOrigin.Manual => "saisi manuellement", _ => "estimé" }
        : "";
}
