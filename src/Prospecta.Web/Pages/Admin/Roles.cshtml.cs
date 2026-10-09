using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Common;
using Prospecta.Application.Security;
using Prospecta.Application.Users;

namespace Prospecta.Web.Pages.Admin;

public class RolesModel(UserAdminService service) : AppPage
{
    public IReadOnlyList<RoleDto> RoleList { get; private set; } = [];

    public async Task OnGetAsync() => RoleList = await service.ListRolesAsync();

    public async Task<IActionResult> OnPostAsync(string role)
    {
        var granted = Permissions.All.Where(p => Request.Form.ContainsKey($"p_{role}_{p}")).ToList();
        try
        {
            await service.SetRolePermissionsAsync(role, granted);
            TempData["Ok"] = $"Permissions du rôle {role} enregistrées (effet immédiat).";
        }
        catch (AppException ex) when (ex is ConflictException or ValidationException)
        {
            TempData["Warn"] = ex.Message;
        }

        return RedirectToPage();
    }
}
