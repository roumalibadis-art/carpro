using Prospecta.Domain.Businesses;
using Prospecta.Domain.Common;
using Prospecta.Domain.Geography;

namespace Prospecta.Domain.Prospecting;

public class Campaign : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? Objective { get; set; }
    public Guid? CategoryId { get; set; }
    public BusinessCategory? Category { get; set; }
    public Guid? WilayaId { get; set; }
    public Guid? DairaId { get; set; }
    public Guid? CommuneId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public Guid ManagerUserId { get; set; }
    public int? VisitTarget { get; set; }
    public decimal? Budget { get; set; }
    public string? Notes { get; set; }
    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;
    public Guid? CreatedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<CampaignParticipant> Participants { get; set; } = [];
    public List<CampaignTarget> Targets { get; set; } = [];
}

public class CampaignParticipant : Entity
{
    public Guid CampaignId { get; set; }
    public Campaign? Campaign { get; set; }
    public Guid UserId { get; set; }
}

/// <summary>A business targeted by a campaign, optionally assigned to a salesperson for that campaign (CampaignAssignments).</summary>
public class CampaignTarget : Entity
{
    public Guid CampaignId { get; set; }
    public Campaign? Campaign { get; set; }
    public Guid BusinessId { get; set; }
    public Business? Business { get; set; }
    public Guid? AssignedUserId { get; set; }
}

public class Outing : Entity
{
    public DateOnly Date { get; set; }
    public TimeOnly? DepartureTime { get; set; }
    public int? DurationMinutes { get; set; }
    public string? StartPoint { get; set; }
    public string? Zone { get; set; }
    public Guid? CampaignId { get; set; }
    public Campaign? Campaign { get; set; }
    public Guid ManagerUserId { get; set; }
    public string? Observations { get; set; }
    public OutingStatus Status { get; set; } = OutingStatus.Planned;
    public DateTime UpdatedAt { get; set; }
    public List<OutingParticipant> Participants { get; set; } = [];
}

public class OutingParticipant : Entity
{
    public Guid OutingId { get; set; }
    public Outing? Outing { get; set; }
    public Guid UserId { get; set; }
}

/// <summary>One interaction with a business. A business has many; history is never replaced by the latest one.</summary>
public class Visit : Entity
{
    public Guid BusinessId { get; set; }
    public Business? Business { get; set; }
    public Guid UserId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? OutingId { get; set; }
    public VisitAction Action { get; set; }
    public VisitStatus Status { get; set; } = VisitStatus.Planned;
    /// <summary>True when the action was scheduled in advance (basis of the realisation rate); false for ad-hoc interactions logged after the fact.</summary>
    public bool WasPlanned { get; set; }
    public DateTime ScheduledAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ContactMet { get; set; }
    public InterestLevel? Interest { get; set; }
    public Guid? OutcomeStatusId { get; set; }
    public string? Objections { get; set; }
    public string? NeedIdentified { get; set; }
    public string? RequestedInfo { get; set; }
    public string? NextAction { get; set; }
    public DateOnly? NextFollowUpDate { get; set; }
    public string? Comment { get; set; }
    public string? CancelReason { get; set; }
    public Guid? PostponedFromId { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class FollowUp : Entity
{
    public Guid BusinessId { get; set; }
    public Business? Business { get; set; }
    public Guid AssignedUserId { get; set; }
    public DateOnly DueDate { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Priority Priority { get; set; } = Priority.Normal;
    public FollowUpStatus Status { get; set; } = FollowUpStatus.ToDo;
    public string? Result { get; set; }
    public DateOnly? NextDueDate { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Guid? VisitId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class Expense : Entity
{
    public Guid? OutingId { get; set; }
    public Guid? CampaignId { get; set; }
    public ExpenseKind Kind { get; set; }
    public ExpenseCategory Category { get; set; }
    public decimal Amount { get; set; }
    public string? Description { get; set; }
    public DateOnly Date { get; set; }
    public Guid? UserId { get; set; }
    /// <summary>Reference of the supporting document (no file storage yet).</summary>
    public string? ReceiptReference { get; set; }
}

public class Notification : Entity
{
    public Guid UserId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }
    public string? Link { get; set; }
    public DateTime? ReadAt { get; set; }
    /// <summary>Unique per user: guarantees a reminder is sent once per follow-up and day.</summary>
    public string DedupKey { get; set; } = string.Empty;
}

public class SystemFlag
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
