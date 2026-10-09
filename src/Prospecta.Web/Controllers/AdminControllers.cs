using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Businesses;
using Prospecta.Application.Common;
using Prospecta.Application.Dashboard;
using Prospecta.Application.Duplicates;
using Prospecta.Application.Imports;
using Prospecta.Application.Reference;
using Prospecta.Application.Users;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Controllers;

[ApiController, Route("api/v1")]
public sealed class ReferenceController(ReferenceService service) : ControllerBase
{
    [HttpGet("geo")]
    public async Task<IActionResult> Geo(GeoLevel? level, Guid? parentId, bool includeInactive = false, CancellationToken ct = default) => Ok(await service.ListGeoAsync(level, parentId, includeInactive, ct));

    [HttpPost("geo")]
    public async Task<IActionResult> CreateGeo(GeoSave input, CancellationToken ct) => Ok(await service.SaveGeoAsync(null, input, ct));

    [HttpPut("geo/{id:guid}")]
    public async Task<IActionResult> UpdateGeo(Guid id, GeoSave input, CancellationToken ct) => Ok(await service.SaveGeoAsync(id, input, ct));

    [HttpGet("categories")]
    public async Task<IActionResult> Categories(Guid? parentId, bool rootsOnly = false, bool includeInactive = false, CancellationToken ct = default) => Ok(await service.ListCategoriesAsync(parentId, rootsOnly, includeInactive, ct));

    public sealed record CategoryRequest(string Name, Guid? ParentId, bool IsActive = true);

    [HttpPost("categories")]
    public async Task<IActionResult> CreateCategory(CategoryRequest r, CancellationToken ct) => Ok(await service.SaveCategoryAsync(null, r.Name, r.ParentId, r.IsActive, ct));

    [HttpPut("categories/{id:guid}")]
    public async Task<IActionResult> UpdateCategory(Guid id, CategoryRequest r, CancellationToken ct) => Ok(await service.SaveCategoryAsync(id, r.Name, r.ParentId, r.IsActive, ct));

    [HttpGet("statuses")]
    public async Task<IActionResult> Statuses(StatusKind? kind, bool includeInactive = false, CancellationToken ct = default) => Ok(await service.ListStatusesAsync(kind, includeInactive, ct));

    public sealed record StatusSave(StatusKind Kind, string? Code, string Label, int SortOrder = 100, bool IsActive = true);

    [HttpPost("statuses")]
    public async Task<IActionResult> CreateStatus(StatusSave r, CancellationToken ct) => Ok(await service.SaveStatusAsync(null, r.Kind, r.Code ?? "", r.Label, r.SortOrder, r.IsActive, ct));

    [HttpPut("statuses/{id:guid}")]
    public async Task<IActionResult> UpdateStatus(Guid id, StatusSave r, CancellationToken ct) => Ok(await service.SaveStatusAsync(id, r.Kind, r.Code ?? "", r.Label, r.SortOrder, r.IsActive, ct));
}

[ApiController, Route("api/v1/duplicates")]
public sealed class DuplicatesController(DuplicateService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(DuplicateStatus status = DuplicateStatus.Pending, int page = 1, int pageSize = 25, CancellationToken ct = default) => Ok(await service.ListAsync(status, page, pageSize, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Compare(Guid id, CancellationToken ct) => Ok(await service.CompareAsync(id, ct));

    [HttpPost("{id:guid}/dismiss")]
    public async Task<IActionResult> Dismiss(Guid id, CancellationToken ct)
    {
        await service.DismissAsync(id, ct);
        return NoContent();
    }

    public sealed record MergeRequest(Guid SurvivorId, string[]? UseOtherFields);

    [HttpPost("{id:guid}/merge")]
    public async Task<IActionResult> Merge(Guid id, MergeRequest r, CancellationToken ct) =>
        Ok(new { survivorId = await service.MergeAsync(id, r.SurvivorId, r.UseOtherFields?.ToHashSet(), ct) });

    [HttpPost("rescan")]
    public async Task<IActionResult> Rescan(int max = 500, CancellationToken ct = default) => Ok(new { pairs = await service.RescanAsync(max, ct) });
}

[ApiController, Route("api/v1/imports")]
public sealed class ImportsController(ImportService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await service.ListAsync(ct));

    [HttpPost, RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        await using var s = file.OpenReadStream();
        return Ok(await service.UploadAsync(s, file.FileName, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await service.GetAsync(id, ct));

    [HttpGet("{id:guid}/rows")]
    public async Task<IActionResult> Rows(Guid id, ImportRowStatus? status, int page = 1, int pageSize = 50, CancellationToken ct = default) => Ok(await service.RowsAsync(id, status, page, pageSize, ct));

    [HttpPut("{id:guid}/mapping")]
    public async Task<IActionResult> Map(Guid id, Dictionary<string, string> mapping, CancellationToken ct) => Ok(await service.MapAsync(id, mapping, ct));

    public sealed record CommitRequest(bool IncludePotentialDuplicates);

    [HttpPost("{id:guid}/commit")]
    public async Task<IActionResult> Commit(Guid id, CommitRequest r, CancellationToken ct) => Ok(await service.CommitAsync(id, r.IncludePotentialDuplicates, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        await service.CancelAsync(id, ct);
        return NoContent();
    }
}

[ApiController, Route("api/v1")]
public sealed class AdminController(UserAdminService users, DashboardService dashboard, AuditQueryService audit, SavedFilterService filters) : ControllerBase
{
    [HttpGet("users")]
    public async Task<IActionResult> Users(string? search, int page = 1, int pageSize = 25, CancellationToken ct = default) => Ok(await users.ListAsync(search, page, pageSize, ct));

    [HttpGet("users/assignable")]
    public async Task<IActionResult> Assignable(CancellationToken ct) => Ok((await users.ListAssignableAsync(ct)).Select(u => new { id = u.Id, name = u.Name }));

    public sealed record CreateUserRequest(string Email, string FullName, string Password, string Role, Guid? ManagerId);
    public sealed record UpdateUserRequest(string FullName, bool IsActive, string Role, Guid? ManagerId);
    public sealed record PasswordRequest(string NewPassword);

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser(CreateUserRequest r) => Ok(await users.CreateAsync(r.Email, r.FullName, r.Password, r.Role, r.ManagerId));

    [HttpPut("users/{id:guid}")]
    public async Task<IActionResult> UpdateUser(Guid id, UpdateUserRequest r) => Ok(await users.UpdateAsync(id, r.FullName, r.IsActive, r.Role, r.ManagerId));

    [HttpPost("users/{id:guid}/password")]
    public async Task<IActionResult> ResetPassword(Guid id, PasswordRequest r)
    {
        await users.ResetPasswordAsync(id, r.NewPassword);
        return NoContent();
    }

    [HttpGet("roles")]
    public async Task<IActionResult> Roles() => Ok(await users.ListRolesAsync());

    [HttpPut("roles/{name}/permissions")]
    public async Task<IActionResult> SetPermissions(string name, string[] permissions)
    {
        await users.SetRolePermissionsAsync(name, permissions);
        return NoContent();
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard([FromQuery] BusinessFilter filter, CancellationToken ct) => Ok(await dashboard.GetAsync(filter, ct));

    [HttpGet("audit")]
    public async Task<IActionResult> Audit(string? action, string? user, DateTime? from, DateTime? to, int page = 1, int pageSize = 50, CancellationToken ct = default) => Ok(await audit.ListAsync(action, user, from, to, page, pageSize, ct));

    [HttpGet("saved-filters")]
    public async Task<IActionResult> SavedFilters(CancellationToken ct) => Ok(await filters.ListAsync(ct));

    public sealed record SaveFilterRequest(string Name, BusinessFilter Filter, SavedFilterScope Scope = SavedFilterScope.Personal);

    [HttpPost("saved-filters")]
    public async Task<IActionResult> SaveFilter(SaveFilterRequest r, CancellationToken ct) => Ok(new { id = await filters.SaveAsync(r.Name, r.Filter, r.Scope, ct) });

    [HttpDelete("saved-filters/{id:guid}")]
    public async Task<IActionResult> DeleteFilter(Guid id, CancellationToken ct)
    {
        await filters.DeleteAsync(id, ct);
        return NoContent();
    }
}
