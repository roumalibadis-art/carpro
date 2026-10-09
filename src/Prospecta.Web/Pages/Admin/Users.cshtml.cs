using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Common;
using Prospecta.Application.Security;
using Prospecta.Application.Users;

namespace Prospecta.Web.Pages.Admin;

public class UsersModel(UserAdminService service, UiLookups lookups) : AppPage
{
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(Name = "Page", SupportsGet = true)] public int PageNo { get; set; } = 1;
    [BindProperty] public string Email { get; set; } = string.Empty;
    [BindProperty] public string FullName { get; set; } = string.Empty;
    [BindProperty] public string Password { get; set; } = string.Empty;
    [BindProperty] public string Role { get; set; } = Roles.Salesperson;
    [BindProperty] public Guid? ManagerId { get; set; }
    public PagedResult<UserDto> Result { get; private set; } = new([], 0, 1, 25);
    public List<SelectListItem> Managers { get; private set; } = [];

    private async Task LoadAsync()
    {
        Result = await service.ListAsync(Search, PageNo, 25);
        Managers = await lookups.UsersAsync(ManagerId, "— aucun —");
    }

    public Task OnGetAsync() => LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        if (await TryAsync(() => service.CreateAsync(Email, FullName, Password, Role, ManagerId)))
        {
            TempData["Ok"] = "Utilisateur créé.";
            return RedirectToPage();
        }

        await LoadAsync();
        return Page();
    }
}
