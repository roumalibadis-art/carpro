using Prospecta.Application.Businesses;
using Prospecta.Application.Collection;
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
        app.MapGet("/ui/map-points", async ([AsParameters] MapQuery q, BusinessService svc, CancellationToken ct) =>
        {
            var (points, total) = await svc.MapPointsAsync(new BusinessFilter { WilayaId = q.WilayaId, DairaId = q.DairaId, CommuneId = q.CommuneId, CategoryId = q.CategoryId, CensusStatusId = q.CensusStatusId, CampaignId = q.CampaignId, ResponsibleUserId = q.UserId, Search = q.Search }, 2000, ct);
            return Results.Ok(new { total, shown = points.Count, points });
        });
        app.MapGet("/ui/parse-maps", (string? url) => Results.Ok(MapsUrlParser.Parse(url))); // purely local parsing, no outgoing request
        app.MapGet("/ui/categories", async (Guid? parentId, ReferenceService svc, CancellationToken ct) =>
            Results.Ok((await svc.ListCategoriesAsync(parentId, parentId is null, false, ct)).Select(c => new { c.Id, c.Name })));
    }
}

public sealed record MapQuery(Guid? WilayaId, Guid? DairaId, Guid? CommuneId, Guid? CategoryId, Guid? CensusStatusId, Guid? CampaignId, Guid? UserId, string? Search);
