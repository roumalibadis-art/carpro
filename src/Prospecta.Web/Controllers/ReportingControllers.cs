using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Security;
using Prospecta.Application.Reporting;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Controllers;

[ApiController, Route("api/v1/indicators"), Authorize(Policy = Permissions.ReportCreate)]
public sealed class IndicatorsController(IndicatorService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] IndicatorFilter filter, CancellationToken ct) => Ok(await service.ComputeAsync(filter, ct));
}

[ApiController, Route("api/v1/reports"), Authorize(Policy = Permissions.ReportCreate)]
public sealed class ReportsController(ReportService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(bool team = false, ReportType? type = null, int page = 1, int pageSize = 25, CancellationToken ct = default) => Ok(await service.ListAsync(team, type, page, pageSize, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await service.GetAsync(id, ct));

    public sealed record GenerateRequest(ReportType Type, ReportParameters Parameters, bool Share = false);

    [HttpPost("preview")]
    public async Task<IActionResult> Preview(GenerateRequest r, CancellationToken ct) => Ok(await service.PreviewAsync(r.Type, r.Parameters, ct));

    [HttpPost]
    public async Task<IActionResult> Save(GenerateRequest r, CancellationToken ct) => Ok(await service.SaveAsync(r.Type, r.Parameters, r.Share, null, ct));

    [HttpPost("{id:guid}/duplicate")]
    public async Task<IActionResult> Duplicate(Guid id, ReportParameters? overrides, CancellationToken ct) => Ok(await service.DuplicateAsync(id, overrides, ct));

    public sealed record ShareRequest(bool Shared);
    public sealed record ValidateRequest(string? Note);

    [HttpPost("{id:guid}/share")]
    public async Task<IActionResult> Share(Guid id, ShareRequest r, CancellationToken ct)
    {
        await service.SetSharedAsync(id, r.Shared, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/validate")]
    public async Task<IActionResult> Validate(Guid id, ValidateRequest r, CancellationToken ct)
    {
        await service.ValidateAsync(id, r.Note, ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/export")]
    public async Task<IActionResult> Export(Guid id, string format = "pdf", CancellationToken ct = default)
    {
        var file = await service.ExportAsync(id, format, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }
}
