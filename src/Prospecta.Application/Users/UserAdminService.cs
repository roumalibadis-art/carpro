using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Common;
using Prospecta.Application.Identity;
using Prospecta.Application.Security;

namespace Prospecta.Application.Users;

public sealed record UserDto(Guid Id, string Email, string FullName, string? Role, bool IsActive, Guid? ManagerId, DateTime CreatedAt, DateTime? LastLoginAt);
public sealed record RoleDto(string Name, IReadOnlyList<string> Permissions);

public sealed class UserAdminService(
    UserManager<ApplicationUser> users, RoleManager<IdentityRole<Guid>> roles, IAppDbContext db, ICurrentUser current,
    IAuditService audit, IPermissionStore permissionStore, TimeProvider clock)
{
    private void Require(string permission)
    {
        if (!current.HasPermission(permission)) throw new ForbiddenException();
    }

    public async Task<PagedResult<UserDto>> ListAsync(string? search, int page, int pageSize, CancellationToken ct = default)
    {
        // Managers need the list to assign work; they only get a minimal view through ListAssignableAsync.
        Require(Permissions.UserManage);
        (page, pageSize) = Paging.Clamp(page, pageSize);
        var q = users.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            q = q.Where(u => u.FullName.ToLower().Contains(s) || u.Email!.ToLower().Contains(s));
        }

        var total = await q.CountAsync(ct);
        var list = await q.OrderBy(u => u.FullName).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = new List<UserDto>();
        foreach (var u in list) items.Add(await ToDtoAsync(u));
        return new PagedResult<UserDto>(items, total, page, pageSize);
    }

    /// <summary>Active users that can receive assignments (names only).</summary>
    public async Task<IReadOnlyList<(Guid Id, string Name)>> ListAssignableAsync(CancellationToken ct = default)
    {
        if (!current.HasPermission(Permissions.BusinessAssign) && !current.HasPermission(Permissions.UserManage) && !current.HasPermission(Permissions.BusinessViewAll)) throw new ForbiddenException();
        var list = await users.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.FullName).Select(u => new { u.Id, u.FullName }).Take(500).ToListAsync(ct);
        return list.Select(u => (u.Id, u.FullName)).ToList();
    }

    private async Task<UserDto> ToDtoAsync(ApplicationUser u) =>
        new(u.Id, u.Email!, u.FullName, (await users.GetRolesAsync(u)).FirstOrDefault(), u.IsActive, u.ManagerId, u.CreatedAt, u.LastLoginAt);

    public async Task<UserDto> GetAsync(Guid id)
    {
        Require(Permissions.UserManage);
        return await ToDtoAsync(await users.FindByIdAsync(id.ToString()) ?? throw new NotFoundException());
    }

    public async Task<UserDto> CreateAsync(string email, string fullName, string password, string role, Guid? managerId)
    {
        Require(Permissions.UserManage);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(fullName) || fullName.Length > 120) errors.Add("Le nom complet est obligatoire (120 caractères max).");
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || email.Length > 200) errors.Add("Adresse e-mail invalide.");
        if (!Roles.All.Contains(role) && !await roles.RoleExistsAsync(role)) errors.Add("Rôle inconnu.");
        if (managerId is not null && await users.FindByIdAsync(managerId.ToString()!) is null) errors.Add("Responsable inconnu.");
        if (errors.Count > 0) throw new ValidationException(errors);

        var user = new ApplicationUser { UserName = email.Trim(), Email = email.Trim(), FullName = fullName.Trim(), ManagerId = managerId, CreatedAt = clock.GetUtcNow().UtcDateTime, EmailConfirmed = true };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded) throw new ValidationException(result.Errors.Select(Translate).ToList());
        await users.AddToRoleAsync(user, role);
        audit.Record("user.create", "User", user.Id, $"{user.Email}; role={role}");
        await db.SaveChangesAsync();
        return await ToDtoAsync(user);
    }

    public async Task<UserDto> UpdateAsync(Guid id, string fullName, bool isActive, string role, Guid? managerId)
    {
        Require(Permissions.UserManage);
        var user = await users.FindByIdAsync(id.ToString()) ?? throw new NotFoundException();
        if (string.IsNullOrWhiteSpace(fullName)) throw new ValidationException("Le nom complet est obligatoire.");
        if (!await roles.RoleExistsAsync(role)) throw new ValidationException("Rôle inconnu.");
        if (managerId == id) throw new ValidationException("Un utilisateur ne peut pas être son propre responsable.");

        var currentRole = (await users.GetRolesAsync(user)).FirstOrDefault();
        var loosesAdmin = currentRole == Roles.Admin && (role != Roles.Admin || !isActive);
        if (loosesAdmin && await ActiveAdminCountAsync() <= 1) throw new ConflictException("Impossible : ce compte est le dernier administrateur actif.");
        if (id == current.Id && !isActive) throw new ConflictException("Vous ne pouvez pas désactiver votre propre compte.");

        var wasActive = user.IsActive;
        user.FullName = fullName.Trim(); user.IsActive = isActive; user.ManagerId = managerId;
        if (currentRole != role)
        {
            if (currentRole is not null) await users.RemoveFromRoleAsync(user, currentRole);
            await users.AddToRoleAsync(user, role);
        }

        var result = await users.UpdateAsync(user);
        if (!result.Succeeded) throw new ValidationException(result.Errors.Select(e => e.Description).ToList());
        if (wasActive && !isActive) await users.UpdateSecurityStampAsync(user); // invalidates existing sessions
        audit.Record("user.update", "User", user.Id, $"role={role}; active={isActive}");
        await db.SaveChangesAsync();
        return await ToDtoAsync(user);
    }

    private async Task<int> ActiveAdminCountAsync() => (await users.GetUsersInRoleAsync(Roles.Admin)).Count(u => u.IsActive);

    public async Task ResetPasswordAsync(Guid id, string newPassword)
    {
        Require(Permissions.UserManage);
        var user = await users.FindByIdAsync(id.ToString()) ?? throw new NotFoundException();
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded) throw new ValidationException(result.Errors.Select(Translate).ToList());
        await users.UpdateSecurityStampAsync(user);
        audit.Record("user.reset_password", "User", user.Id);
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync()
    {
        Require(Permissions.RoleManage);
        var result = new List<RoleDto>();
        foreach (var r in await roles.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync())
        {
            var claims = await roles.GetClaimsAsync(r);
            result.Add(new RoleDto(r.Name!, claims.Where(c => c.Type == Permissions.ClaimType).Select(c => c.Value).OrderBy(v => v).ToList()));
        }

        return result;
    }

    public async Task SetRolePermissionsAsync(string roleName, IReadOnlyCollection<string> permissions)
    {
        Require(Permissions.RoleManage);
        var role = await roles.FindByNameAsync(roleName) ?? throw new NotFoundException();
        var unknown = permissions.Except(Permissions.All).ToList();
        if (unknown.Count > 0) throw new ValidationException($"Permission inconnue : {unknown[0]}.");
        if (roleName == Roles.Admin && (!permissions.Contains(Permissions.UserManage) || !permissions.Contains(Permissions.RoleManage)))
            throw new ConflictException("Le rôle Administrateur doit conserver la gestion des utilisateurs et des rôles.");

        var existing = (await roles.GetClaimsAsync(role)).Where(c => c.Type == Permissions.ClaimType).ToList();
        foreach (var c in existing.Where(c => !permissions.Contains(c.Value))) await roles.RemoveClaimAsync(role, c);
        foreach (var p in permissions.Where(p => existing.All(c => c.Value != p))) await roles.AddClaimAsync(role, new System.Security.Claims.Claim(Permissions.ClaimType, p));
        permissionStore.Invalidate();
        audit.Record("role.permissions", "Role", roleName, string.Join(",", permissions.OrderBy(p => p)));
        await db.SaveChangesAsync();
    }

    private static string Translate(IdentityError e) => e.Code switch
    {
        "DuplicateUserName" or "DuplicateEmail" => "Cette adresse e-mail est déjà utilisée.",
        "PasswordTooShort" => "Mot de passe trop court (10 caractères minimum).",
        "PasswordRequiresDigit" => "Le mot de passe doit contenir un chiffre.",
        "PasswordRequiresLower" => "Le mot de passe doit contenir une minuscule.",
        "PasswordRequiresUpper" => "Le mot de passe doit contenir une majuscule.",
        "PasswordRequiresNonAlphanumeric" => "Le mot de passe doit contenir un caractère spécial.",
        _ => e.Description,
    };
}
