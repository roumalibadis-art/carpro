using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Businesses;
using Prospecta.Application.Common;
using Prospecta.Application.Dashboard;
using Prospecta.Application.Security;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Businesses;

public class IndexModel(BusinessService service, ExportService export, SavedFilterService savedFilters, UiLookups lookups, Prospecta.Application.Prospecting.CampaignService campaignService) : AppPage
{
    [BindProperty(SupportsGet = true)] public BusinessFilter Filter { get; set; } = new();
    [BindProperty(SupportsGet = true)] public Guid? ViewId { get; set; }
    public PagedResult<BusinessListItem> Result { get; private set; } = new([], 0, 1, 25);
    public IReadOnlyList<SavedFilterDto> Views { get; private set; } = [];

    public List<SelectListItem> Wilayas = [], Dairas = [], Communes = [], Districts = [], Categories = [], SubCategories = [];
    public List<SelectListItem> CampaignChoices = [];
    public List<SelectListItem> CensusStatuses = [], ProcessingStatuses = [], OutcomeStatuses = [], Users = [];

    private async Task LoadAsync()
    {
        Views = await savedFilters.ListAsync();
        if (ViewId is { } vid && Views.FirstOrDefault(v => v.Id == vid) is { } view) Filter = view.Filter;
        Filter.PageSize = Filter.PageSize <= 0 ? 25 : Filter.PageSize;
        Result = await service.SearchAsync(Filter, HttpContext.RequestAborted);

        Wilayas = await lookups.GeoAsync(GeoLevel.Wilaya, null, Filter.WilayaId, "Toutes");
        Dairas = Filter.WilayaId is null ? UiLookups.WithBlank([], "Toutes") : await lookups.GeoAsync(GeoLevel.Daira, Filter.WilayaId, Filter.DairaId, "Toutes");
        Communes = Filter.DairaId is null ? UiLookups.WithBlank([], "Toutes") : await lookups.GeoAsync(GeoLevel.Commune, Filter.DairaId, Filter.CommuneId, "Toutes");
        Districts = Filter.CommuneId is null ? UiLookups.WithBlank([], "Tous") : await lookups.GeoAsync(GeoLevel.Quartier, Filter.CommuneId, Filter.DistrictId, "Tous");
        Categories = await lookups.CategoriesAsync(null, Filter.CategoryId, "Toutes");
        SubCategories = Filter.CategoryId is null ? UiLookups.WithBlank([], "Toutes") : await lookups.CategoriesAsync(Filter.CategoryId, Filter.SubCategoryId, "Toutes");
        CensusStatuses = await lookups.StatusesAsync(StatusKind.Census, Filter.CensusStatusId, "Tous");
        ProcessingStatuses = await lookups.StatusesAsync(StatusKind.Processing, Filter.ProcessingStatusId, "Tous");
        OutcomeStatuses = await lookups.StatusesAsync(StatusKind.Outcome, Filter.OutcomeStatusId, "Tous");
        Users = await lookups.UsersAsync(Filter.ResponsibleUserId, "Tous");
        CampaignChoices = Can(Prospecta.Application.Security.Permissions.CampaignManage) ? await lookups.CampaignsAsync(campaignService, null, "— campagne —") : [];
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnGetExportAsync(string format = "csv")
    {
        var file = await export.ExportAsync(Filter, format, HttpContext.RequestAborted);
        return File(file.Content, file.ContentType, file.FileName);
    }

    public async Task<IActionResult> OnPostBulkAsync(Guid[] ids, string bulk, Guid? userId, Guid? statusId, StatusKind? kind, Guid? campaignId, string? returnQuery)
    {
        try
        {
            if (ids.Length == 0) throw new ValidationException("Sélectionnez au moins une entreprise.");
            switch (bulk)
            {
                case "assign":
                    TempData["Ok"] = $"{await service.AssignAsync(ids, userId ?? Guid.Empty)} entreprise(s) affectée(s).";
                    break;
                case "status":
                    TempData["Ok"] = $"{await service.BulkSetStatusAsync(ids, kind ?? StatusKind.Census, statusId ?? Guid.Empty)} entreprise(s) mise(s) à jour.";
                    break;
                case "campaign":
                    if (campaignId is null) throw new ValidationException("Choisissez une campagne.");
                    TempData["Ok"] = $"{await campaignService.AddTargetsAsync(campaignId.Value, ids, userId)} entreprise(s) ajoutée(s) à la campagne.";
                    break;
                default:
                    throw new ValidationException("Action inconnue.");
            }
        }
        catch (AppException ex) when (ex is ValidationException or ConflictException or ForbiddenException)
        {
            TempData["Warn"] = ex.Message;
        }

        return Redirect("/Businesses" + (string.IsNullOrEmpty(returnQuery) ? "" : "?" + returnQuery.TrimStart('?')));
    }

    public async Task<IActionResult> OnPostSaveViewAsync(string viewName, bool shared, string? returnQuery)
    {
        // The filter to store is re-read from the posted query string, never from a client-supplied JSON blob.
        var f = new BusinessFilter();
        var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery("?" + returnQuery?.TrimStart('?'));
        await TryUpdateModelAsync(f, "Filter", new QueryStringValueProvider(BindingSource.Query, new Microsoft.AspNetCore.Http.QueryCollection(parsed), System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            await savedFilters.SaveAsync(viewName, f, shared ? Prospecta.Domain.Common.SavedFilterScope.Shared : Prospecta.Domain.Common.SavedFilterScope.Personal);
            TempData["Ok"] = "Vue enregistrée.";
        }
        catch (AppException ex) when (ex is ValidationException or ForbiddenException)
        {
            TempData["Warn"] = ex.Message;
        }

        return Redirect("/Businesses" + (string.IsNullOrEmpty(returnQuery) ? "" : "?" + returnQuery.TrimStart('?')));
    }

    public async Task<IActionResult> OnPostDeleteViewAsync(Guid id)
    {
        await savedFilters.DeleteAsync(id);
        TempData["Ok"] = "Vue supprimée.";
        return RedirectToPage();
    }

    public string QueryWith(string sortBy) =>
        "?" + string.Join("&", Request.Query.Where(kv => kv.Key is not ("Filter.SortBy" or "Filter.SortDesc" or "Filter.Page"))
            .SelectMany(kv => kv.Value.Select(v => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(v ?? "")}"))
            .Append($"Filter.SortBy={sortBy}").Append($"Filter.SortDesc={(Filter.SortBy == sortBy && !Filter.SortDesc ? "true" : "false")}"));
}
