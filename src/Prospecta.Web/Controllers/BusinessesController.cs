using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Security;
using Prospecta.Application.Businesses;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Controllers;

public sealed record AssignRequest(Guid[] BusinessIds, Guid UserId);
public sealed record StatusRequest(StatusKind Kind, Guid StatusId);
public sealed record BulkStatusRequest(Guid[] BusinessIds, StatusKind Kind, Guid StatusId);
public sealed record FlagsRequest(bool? HasContactError, bool? ChangeReported);
public sealed record DeleteRequest(string? Reason);

[ApiController, Route("api/v1/businesses")]
public sealed class BusinessesController(BusinessService service, ExportService export) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] BusinessFilter filter, CancellationToken ct) => Ok(await service.SearchAsync(filter, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await service.GetAsync(id, ct));

    [HttpPost]
    public async Task<IActionResult> Create(BusinessInput input, CancellationToken ct)
    {
        var result = await service.CreateAsync(input, ct);
        return CreatedAtAction(nameof(Get), new { id = result.Business.Id }, result);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, BusinessInput input, CancellationToken ct) => Ok(await service.UpdateAsync(id, input, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromBody] DeleteRequest? body, CancellationToken ct)
    {
        await service.DeleteAsync(id, body?.Reason, ct);
        return NoContent();
    }

    [Authorize(Policy = Permissions.BusinessVerify), HttpPost("{id:guid}/verify")]
    public async Task<IActionResult> Verify(Guid id, CancellationToken ct)
    {
        await service.VerifyAsync(id, ct);
        return NoContent();
    }

    [HttpPut("{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, StatusRequest r, CancellationToken ct)
    {
        await service.SetStatusAsync(id, r.Kind, r.StatusId, ct);
        return NoContent();
    }

    [HttpPut("{id:guid}/flags")]
    public async Task<IActionResult> SetFlags(Guid id, FlagsRequest r, CancellationToken ct)
    {
        await service.SetFlagsAsync(id, r.HasContactError, r.ChangeReported, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/history")]
    public async Task<IActionResult> History(Guid id, int page = 1, int pageSize = 50, CancellationToken ct = default) => Ok(await service.HistoryAsync(id, page, pageSize, ct));

    [Authorize(Policy = Permissions.BusinessAssign), HttpPost("assign")]
    public async Task<IActionResult> Assign(AssignRequest r, CancellationToken ct) => Ok(new { assigned = await service.AssignAsync(r.BusinessIds, r.UserId, ct) });

    [HttpDelete("{id:guid}/assignments/{userId:guid}")]
    public async Task<IActionResult> Unassign(Guid id, Guid userId, CancellationToken ct)
    {
        await service.UnassignAsync(id, userId, ct);
        return NoContent();
    }

    [HttpPost("bulk-status")]
    public async Task<IActionResult> BulkStatus(BulkStatusRequest r, CancellationToken ct) => Ok(new { updated = await service.BulkSetStatusAsync(r.BusinessIds, r.Kind, r.StatusId, ct) });

    [Authorize(Policy = Permissions.BusinessExport), HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] BusinessFilter filter, string format = "csv", CancellationToken ct = default)
    {
        var file = await export.ExportAsync(filter, format, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }
}
