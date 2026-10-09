using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Prospecta.Application.Users;

namespace Prospecta.Web.Pages.Admin;

public class UserEditModel(UserAdminService service, UiLookups lookups) : AppPage
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty] public string FullName { get; set; } = string.Empty;
    [BindProperty] public string Role { get; set; } = string.Empty;
    [BindProperty] public bool IsActive { get; set; }
    [BindProperty] public Guid? ManagerId { get; set; }
    [BindProperty] public string NewPassword { get; set; } = string.Empty;
    public UserDto Data { get; private set; } = default!;
    public List<SelectListItem> Managers { get; private set; } = [];

    private async Task LoadAsync()
    {
        Data = await service.GetAsync(Id);
        Managers = (await lookups.UsersAsync(ManagerId ?? Data.ManagerId, "— aucun —")).Where(m => m.Value != Id.ToString()).ToList();
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        (FullName, Role, IsActive, ManagerId) = (Data.FullName, Data.Role ?? "Salesperson", Data.IsActive, Data.ManagerId);
        Managers = (await lookups.UsersAsync(ManagerId, "— aucun —")).Where(m => m.Value != Id.ToString()).ToList();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await TryAsync(() => service.UpdateAsync(Id, FullName, IsActive, Role, ManagerId)))
        {
            TempData["Ok"] = "Utilisateur mis à jour.";
            return Redirect("/Admin/Users");
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        if (await TryAsync(() => service.ResetPasswordAsync(Id, NewPassword)))
        {
            TempData["Ok"] = "Mot de passe réinitialisé ; les sessions existantes sont révoquées.";
            return Redirect($"/Admin/UserEdit/{Id}");
        }

        await LoadAsync();
        return Page();
    }
}
