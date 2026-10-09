using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Security;
using Prospecta.Application.Businesses;
using Prospecta.Application.Collection;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Controllers;

[ApiController, Route("api/v1/collection"), Authorize]
public sealed class CollectionController(CollectionService service, UrlInspectionService urls, BusinessService businesses) : ControllerBase
{
    [Authorize(Policy = Permissions.CollectionRun), HttpGet("connectors")]
    public async Task<IActionResult> Connectors(CancellationToken ct) => Ok(await service.ConnectorsAsync(ct));

    [Authorize(Policy = Permissions.CollectionRun), HttpPost("search")]
    public async Task<IActionResult> Search(CollectionRequest r, CancellationToken ct) => Ok(await service.SearchAsync(r, null, ct));

    [Authorize(Policy = Permissions.CollectionRun), HttpGet("jobs")]
    public async Task<IActionResult> Jobs(int page = 1, int pageSize = 25, CancellationToken ct = default) => Ok(await service.ListJobsAsync(page, pageSize, ct));

    [Authorize(Policy = Permissions.CollectionRun), HttpGet("jobs/{id:guid}")]
    public async Task<IActionResult> Job(Guid id, CollectionResultStatus? status, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var (job, results) = await service.GetAsync(id, status, page, pageSize, ct);
        return Ok(new { job, results });
    }

    public sealed record ImportRequest(Guid[]? ResultIds, bool IncludeDuplicates);
    public sealed record RejectRequest(Guid[] ResultIds);

    [Authorize(Policy = Permissions.CollectionRun), HttpPost("jobs/{id:guid}/import")]
    public async Task<IActionResult> Import(Guid id, ImportRequest r, CancellationToken ct)
    {
        var (imported, skipped) = await service.ImportAsync(id, r.ResultIds, r.IncludeDuplicates, ct);
        return Ok(new { imported, skipped });
    }

    [Authorize(Policy = Permissions.CollectionRun), HttpPost("jobs/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, RejectRequest r, CancellationToken ct)
    {
        await service.RejectAsync(id, r.ResultIds, ct);
        return NoContent();
    }

    [Authorize(Policy = Permissions.CollectionRun), HttpPost("jobs/{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct) => Ok(await service.RetryAsync(id, ct));

    public sealed record InspectRequest(string Url);

    [HttpPost("inspect-url")]
    public async Task<IActionResult> Inspect(InspectRequest r, CancellationToken ct) => Ok(await urls.InspectAsync(r.Url, ct));

    public sealed record FromUrlRequest(BusinessInput Business, string? SourceUrl, string Kind);

    /// <summary>Creates the business the user reviewed after inspecting a URL; the source (public page or map link) is recorded.</summary>
    [HttpPost("create-from-url")]
    public async Task<IActionResult> CreateFromUrl(FromUrlRequest r, CancellationToken ct) =>
        Ok(await businesses.CreateWithSourceAsync(r.Business, FieldOrigin.External, new BusinessService.SourceInfo(r.Kind == "maps" ? SourceType.PublicUrl : SourceType.PublicWebsite, r.Kind == "maps" ? "map-link" : "website", null, r.SourceUrl), ct));
}

[ApiController, Route("api/v1")]
public sealed class ToolsController(TemplateService templates, RefreshService refresh, BusinessService businesses) : ControllerBase
{
    [HttpGet("templates/businesses")]
    public async Task<IActionResult> BusinessTemplate(string format = "xlsx", CancellationToken ct = default)
    {
        var f = await templates.BusinessTemplateAsync(format, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpGet("templates/geography")]
    public IActionResult GeographyTemplate() { var f = templates.GeographyTemplate(); return File(f.Content, f.ContentType, f.FileName); }

    [HttpGet("businesses/{id:guid}/refresh-check")]
    public async Task<IActionResult> Check(Guid id, CancellationToken ct) => Ok(await refresh.CheckAsync(id, ct));

    public sealed record ApplyRequest(string[] Fields);

    [HttpPost("businesses/{id:guid}/refresh-apply")]
    public async Task<IActionResult> Apply(Guid id, ApplyRequest r, CancellationToken ct)
    {
        var (applied, blocked) = await refresh.ApplyAsync(id, r.Fields, ct);
        return Ok(new { applied, blocked });
    }

    [HttpGet("map/points")]
    public async Task<IActionResult> Points([FromQuery] BusinessFilter filter, CancellationToken ct)
    {
        var (points, total) = await businesses.MapPointsAsync(filter, 2000, ct);
        return Ok(new { total, shown = points.Count, points });
    }
}
