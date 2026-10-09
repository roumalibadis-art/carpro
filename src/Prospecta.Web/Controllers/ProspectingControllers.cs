using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Businesses;
using Prospecta.Application.Prospecting;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Controllers;

[ApiController, Route("api/v1/campaigns")]
public sealed class CampaignsController(CampaignService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CampaignStatus? status, string? search, int page = 1, int pageSize = 25, CancellationToken ct = default) => Ok(await service.ListAsync(status, search, page, pageSize, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await service.GetAsync(id, ct));

    [HttpPost]
    public async Task<IActionResult> Create(CampaignInput input, CancellationToken ct)
    {
        var id = await service.SaveAsync(null, input, ct);
        return CreatedAtAction(nameof(Get), new { id }, await service.GetAsync(id, ct));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, CampaignInput input, CancellationToken ct)
    {
        await service.SaveAsync(id, input, ct);
        return Ok(await service.GetAsync(id, ct));
    }

    public sealed record StatusRequest(CampaignStatus Status);

    [HttpPut("{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, StatusRequest r, CancellationToken ct)
    {
        await service.SetStatusAsync(id, r.Status, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/targets")]
    public async Task<IActionResult> Targets(Guid id, int page = 1, int pageSize = 50, CancellationToken ct = default) => Ok(await service.TargetsAsync(id, page, pageSize, ct));

    public sealed record AddTargetsRequest(Guid[] BusinessIds, Guid? AssigneeId);

    [HttpPost("{id:guid}/targets")]
    public async Task<IActionResult> AddTargets(Guid id, AddTargetsRequest r, CancellationToken ct) => Ok(new { changed = await service.AddTargetsAsync(id, r.BusinessIds, r.AssigneeId, ct) });

    public sealed record AddByFilterRequest(BusinessFilter Filter, Guid? AssigneeId);

    [HttpPost("{id:guid}/targets/by-filter")]
    public async Task<IActionResult> AddByFilter(Guid id, AddByFilterRequest r, CancellationToken ct) => Ok(new { changed = await service.AddTargetsByFilterAsync(id, r.Filter, r.AssigneeId, ct) });

    [HttpDelete("{id:guid}/targets/{businessId:guid}")]
    public async Task<IActionResult> RemoveTarget(Guid id, Guid businessId, CancellationToken ct)
    {
        await service.RemoveTargetAsync(id, businessId, ct);
        return NoContent();
    }
}

[ApiController, Route("api/v1/outings")]
public sealed class OutingsController(OutingService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(DateOnly? from, DateOnly? to, Guid? campaignId, int page = 1, int pageSize = 25, CancellationToken ct = default) => Ok(await service.ListAsync(from, to, campaignId, page, pageSize, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await service.GetAsync(id, ct));

    [HttpPost]
    public async Task<IActionResult> Create(OutingInput input, CancellationToken ct)
    {
        var id = await service.SaveAsync(null, input, ct);
        return CreatedAtAction(nameof(Get), new { id }, await service.GetAsync(id, ct));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, OutingInput input, CancellationToken ct)
    {
        await service.SaveAsync(id, input, ct);
        return Ok(await service.GetAsync(id, ct));
    }

    public sealed record CloseRequest(OutingStatus Status, string? Observations);

    [HttpPost("{id:guid}/close")]
    public async Task<IActionResult> Close(Guid id, CloseRequest r, CancellationToken ct)
    {
        await service.CloseAsync(id, r.Status, r.Observations, ct);
        return NoContent();
    }

    [HttpPost("expenses")]
    public async Task<IActionResult> AddExpense(ExpenseInput input, CancellationToken ct) => Ok(new { id = await service.AddExpenseAsync(input, ct) });

    [HttpDelete("expenses/{id:guid}")]
    public async Task<IActionResult> DeleteExpense(Guid id, CancellationToken ct)
    {
        await service.DeleteExpenseAsync(id, ct);
        return NoContent();
    }
}

[ApiController, Route("api/v1/visits")]
public sealed class VisitsController(VisitService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] VisitFilter filter, CancellationToken ct) => Ok(await service.SearchAsync(filter, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await service.GetAsync(id, ct));

    [HttpGet("business/{businessId:guid}")]
    public async Task<IActionResult> ForBusiness(Guid businessId, CancellationToken ct) => Ok(await service.HistoryForBusinessAsync(businessId, ct));

    [HttpPost("plan")]
    public async Task<IActionResult> Plan(VisitInput input, CancellationToken ct) => Ok(await service.PlanAsync(input, ct));

    public sealed record LogRequest(VisitInput Visit, VisitResult Result);

    [HttpPost("log")]
    public async Task<IActionResult> Log(LogRequest r, CancellationToken ct) => Ok(await service.LogDoneAsync(r.Visit, r.Result, ct));

    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, VisitResult r, CancellationToken ct) => Ok(await service.CompleteAsync(id, r, ct));

    public sealed record CancelRequest(string? Reason);
    public sealed record PostponeRequest(DateTime NewScheduledAt);

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancelRequest r, CancellationToken ct)
    {
        await service.CancelAsync(id, r.Reason, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/postpone")]
    public async Task<IActionResult> Postpone(Guid id, PostponeRequest r, CancellationToken ct) => Ok(await service.PostponeAsync(id, r.NewScheduledAt, ct));
}

[ApiController, Route("api/v1/followups")]
public sealed class FollowUpsController(FollowUpService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] FollowUpFilter filter, CancellationToken ct) => Ok(await service.SearchAsync(filter, ct));

    [HttpPost]
    public async Task<IActionResult> Create(FollowUpInput input, CancellationToken ct) => Ok(await service.CreateAsync(input, ct));

    public sealed record CompleteRequest(string? Result, DateOnly? NextDueDate);
    public sealed record PostponeRequest(DateOnly NewDueDate);
    public sealed record CancelRequest(string? Reason);

    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, CompleteRequest r, CancellationToken ct)
    {
        await service.CompleteAsync(id, r.Result, r.NextDueDate, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/postpone")]
    public async Task<IActionResult> Postpone(Guid id, PostponeRequest r, CancellationToken ct)
    {
        await service.PostponeAsync(id, r.NewDueDate, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancelRequest r, CancellationToken ct)
    {
        await service.CancelAsync(id, r.Reason, ct);
        return NoContent();
    }
}

[ApiController, Route("api/v1/notifications")]
public sealed class NotificationsController(NotificationService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(int page = 1, int pageSize = 25, CancellationToken ct = default) => Ok(new { unread = await service.UnreadCountAsync(ct), page = await service.ListAsync(page, pageSize, ct) });

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct)
    {
        await service.MarkReadAsync(id, ct);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll(CancellationToken ct)
    {
        await service.MarkReadAsync(null, ct);
        return NoContent();
    }
}
