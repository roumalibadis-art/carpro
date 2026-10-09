using Prospecta.Domain.Common;

namespace Prospecta.Application.Businesses;

public sealed class BusinessInput
{
    public string Name { get; set; } = string.Empty;
    public string? LegalName { get; set; }
    public string? Description { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? SubCategoryId { get; set; }
    public Guid? WilayaId { get; set; }
    public Guid? DairaId { get; set; }
    public Guid? CommuneId { get; set; }
    public Guid? DistrictId { get; set; }
    public string? Address { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? PlusCode { get; set; }
    public string? Phone { get; set; }
    public string? Website { get; set; }
    public string? GoogleMapsUrl { get; set; }
    public string? SourceUrl { get; set; }
    public string? InternalNotes { get; set; }
    public Priority Priority { get; set; } = Priority.Normal;
}

public sealed class BusinessFilter
{
    public string? Search { get; set; }
    public Guid? WilayaId { get; set; }
    public Guid? DairaId { get; set; }
    public Guid? CommuneId { get; set; }
    public Guid? DistrictId { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? SubCategoryId { get; set; }
    public SourceType? SourceType { get; set; }
    public Guid? ResponsibleUserId { get; set; }
    public DateTime? CollectedFrom { get; set; }
    public DateTime? CollectedTo { get; set; }
    public DateTime? VerifiedFrom { get; set; }
    public DateTime? VerifiedTo { get; set; }
    /// <summary>Records never verified or verified before this date (selection for re-verification).</summary>
    public DateTime? StaleBefore { get; set; }
    public int? MinCompleteness { get; set; }
    public int? MaxCompleteness { get; set; }
    public Guid? CensusStatusId { get; set; }
    public Guid? ProcessingStatusId { get; set; }
    public Guid? OutcomeStatusId { get; set; }
    public Priority? Priority { get; set; }
    public bool? HasPhone { get; set; }
    public bool? HasWebsite { get; set; }
    public bool? HasCoordinates { get; set; }
    public bool? HasContactError { get; set; }
    public bool? ChangeReported { get; set; }
    public bool? PendingDuplicate { get; set; }
    public Guid? CampaignId { get; set; }
    /// <summary>Businesses that have at least one follow-up past its due date.</summary>
    public bool? OverdueFollowUp { get; set; }
    public string SortBy { get; set; } = "name";
    public bool SortDesc { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public sealed record StatusRef(Guid Id, string Code, string Label);

public sealed record BusinessListItem(
    Guid Id, string Name, string? Category, string? SubCategory, string? Wilaya, string? Daira, string? Commune, string? Phone, string? Website,
    StatusRef CensusStatus, StatusRef ProcessingStatus, StatusRef OutcomeStatus, Priority Priority, int Completeness, ConfidenceLevel Confidence,
    DateTime CollectedAt, DateTime? LastVerifiedAt, double? Latitude, double? Longitude, IReadOnlyList<string> Responsible, bool PendingDuplicate);

public sealed record SourceDto(SourceType SourceType, string Provider, string? ExternalId, string? Url, DateTime CollectedAt);
public sealed record AssignmentDto(Guid UserId, string UserName, DateTime Since);
public sealed record HistoryDto(string Field, string? OldValue, string? NewValue, FieldOrigin Origin, string? ChangedBy, string? Reason, DateTime At);

public sealed record BusinessDetail(
    Guid Id, BusinessInput Data, string? Category, string? SubCategory, string? Wilaya, string? Daira, string? Commune, string? District,
    StatusRef CensusStatus, StatusRef ProcessingStatus, StatusRef OutcomeStatus, int Completeness, ConfidenceLevel Confidence,
    DateTime CollectedAt, DateTime? LastVerifiedAt, string? LastVerifiedBy, bool HasContactError, bool ChangeReported, bool IsDemo,
    DateTime CreatedAt, DateTime UpdatedAt, IReadOnlyDictionary<string, FieldOrigin> Origins, IReadOnlyList<SourceDto> Sources,
    IReadOnlyList<AssignmentDto> Assignments, bool CanEdit, bool CanVerify);

public sealed record SaveResult(BusinessDetail Business, IReadOnlyList<string> BlockedFields, IReadOnlyList<string> DuplicateWarnings);
