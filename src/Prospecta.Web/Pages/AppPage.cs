using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Common;
using Prospecta.Application.Reference;
using Prospecta.Application.Users;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages;

public abstract class AppPage : PageModel
{
    /// <summary>Runs a use case and turns expected business errors into form messages (instead of an error page).</summary>
    protected async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (ValidationException v)
        {
            foreach (var e in v.Errors) ModelState.AddModelError(string.Empty, e);
        }
        catch (ConflictException c)
        {
            ModelState.AddModelError(string.Empty, c.Message);
        }
        catch (ForbiddenException f)
        {
            ModelState.AddModelError(string.Empty, f.Message);
        }

        return false;
    }

    public static string Local(DateTime utc) => Prospecta.Application.Prospecting.Dates.UtcToLocal(utc).ToString("dd/MM/yyyy HH:mm");

    protected bool Can(string permission) => User.HasClaim(Prospecta.Application.Security.Permissions.ClaimType, permission);
}

/// <summary>Select-list sources for the filter/edit forms.</summary>
public sealed class UiLookups(ReferenceService reference, UserAdminService users)
{
    public static List<SelectListItem> WithBlank(IEnumerable<SelectListItem> items, string blank = "—") => [new(blank, ""), .. items];

    public async Task<List<SelectListItem>> GeoAsync(GeoLevel level, Guid? parent, Guid? selected, string blank = "—") =>
        WithBlank((await reference.ListGeoAsync(level, parent, false)).Select(g => new SelectListItem(g.Code.Length > 0 ? $"{g.Code} - {g.Name}" : g.Name, g.Id.ToString(), g.Id == selected)), blank);

    public async Task<List<SelectListItem>> CategoriesAsync(Guid? parent, Guid? selected, string blank = "—") =>
        WithBlank((await reference.ListCategoriesAsync(parent, parent is null)).Select(c => new SelectListItem(c.Name, c.Id.ToString(), c.Id == selected)), blank);

    public async Task<List<SelectListItem>> StatusesAsync(StatusKind kind, Guid? selected, string blank = "—") =>
        WithBlank((await reference.ListStatusesAsync(kind)).Select(s => new SelectListItem(s.Label, s.Id.ToString(), s.Id == selected)), blank);

    public async Task<List<SelectListItem>> UsersAsync(Guid? selected, string blank = "—")
    {
        try
        {
            return WithBlank((await users.ListAssignableAsync()).Select(u => new SelectListItem(u.Name, u.Id.ToString(), u.Id == selected)), blank);
        }
        catch (ForbiddenException)
        {
            return WithBlank([], blank);
        }
    }
}

public static class UiLookupsExt
{
    public static async Task<List<SelectListItem>> CampaignsAsync(this UiLookups l, Prospecta.Application.Prospecting.CampaignService svc, Guid? selected, string blank = "—")
    {
        var page = await svc.ListAsync(null, null, 1, 100);
        return UiLookups.WithBlank(page.Items.Where(c => c.Status is Prospecta.Domain.Common.CampaignStatus.Draft or Prospecta.Domain.Common.CampaignStatus.Active)
            .Select(c => new SelectListItem(c.Name, c.Id.ToString(), c.Id == selected)), blank);
    }
}
