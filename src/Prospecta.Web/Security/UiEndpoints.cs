using Prospecta.Application.Reference;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Security;

public static class UiEndpoints
{
    /// <summary>Cookie-authenticated read-only helpers for the pages' dependent dropdowns (Wilaya → Daïra → Commune → Quartier).</summary>
    public static void MapUiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/ui/geo", async (Guid? parentId, GeoLevel? level, ReferenceService svc, CancellationToken ct) =>
            Results.Ok((await svc.ListGeoAsync(level, parentId, false, ct)).Select(g => new { g.Id, g.Name, g.Code })));
        app.MapGet("/ui/categories", async (Guid? parentId, ReferenceService svc, CancellationToken ct) =>
            Results.Ok((await svc.ListCategoriesAsync(parentId, parentId is null, false, ct)).Select(c => new { c.Id, c.Name })));
    }
}
