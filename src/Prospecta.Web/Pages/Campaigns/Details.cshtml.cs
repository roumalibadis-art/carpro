using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Common;
using Prospecta.Application.Prospecting;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Campaigns;

public class DetailsModel(CampaignService service) : AppPage
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty(Name = "Page", SupportsGet = true)] public int PageNo { get; set; } = 1;
    public CampaignDetail Data { get; private set; } = default!;
    public PagedResult<TargetDto> Targets { get; private set; } = new([], 0, 1, 50);

    public async Task OnGetAsync()
    {
        Data = await service.GetAsync(Id);
        Targets = await service.TargetsAsync(Id, PageNo, 50);
    }

    private async Task<IActionResult> Act(Func<Task> action, string ok)
    {
        try { await action(); TempData["Ok"] = ok; }
        catch (AppException ex) when (ex is ValidationException or ConflictException) { TempData["Warn"] = ex.Message; }
        return Redirect($"/Campaigns/Details/{Id}");
    }

    public Task<IActionResult> OnPostStatusAsync(CampaignStatus status) => Act(() => service.SetStatusAsync(Id, status), "État de la campagne mis à jour.");
    public Task<IActionResult> OnPostRemoveAsync(Guid businessId) => Act(() => service.RemoveTargetAsync(Id, businessId), "Entreprise retirée de la campagne.");
}
