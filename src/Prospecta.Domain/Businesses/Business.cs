using Prospecta.Domain.Common;
using Prospecta.Domain.Geography;

namespace Prospecta.Domain.Businesses;

/// <summary>
/// Organization-shared company record. Three independent state dimensions: census (data quality),
/// processing (commercial workflow) and outcome (commercial result). Absent data stays null — never invented.
/// </summary>
public class Business : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? LegalName { get; set; }
    public string NormalizedName { get; set; } = string.Empty;
    public string? Description { get; set; }

    public Guid? CategoryId { get; set; }
    public BusinessCategory? Category { get; set; }
    public Guid? SubCategoryId { get; set; }
    public BusinessCategory? SubCategory { get; set; }

    public Guid? WilayaId { get; set; }
    public GeographicArea? Wilaya { get; set; }
    public Guid? DairaId { get; set; }
    public GeographicArea? Daira { get; set; }
    public Guid? CommuneId { get; set; }
    public GeographicArea? Commune { get; set; }
    public Guid? DistrictId { get; set; }
    public GeographicArea? District { get; set; }

    public string? Address { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? PlusCode { get; set; }
    public string? Phone { get; set; }
    public string? NormalizedPhone { get; set; }
    public string? Website { get; set; }
    public string? WebsiteHost { get; set; }
    public string? GoogleMapsUrl { get; set; }
    public string? SourceUrl { get; set; }
    public string? InternalNotes { get; set; }

    public Guid CensusStatusId { get; set; }
    public StatusValue? CensusStatus { get; set; }
    public Guid ProcessingStatusId { get; set; }
    public StatusValue? ProcessingStatus { get; set; }
    public Guid OutcomeStatusId { get; set; }
    public StatusValue? OutcomeStatus { get; set; }

    public Priority Priority { get; set; } = Priority.Normal;
    public DateTime CollectedAt { get; set; }
    public DateTime? LastVerifiedAt { get; set; }
    public Guid? LastVerifiedByUserId { get; set; }
    public ConfidenceLevel Confidence { get; set; } = ConfidenceLevel.Low;
    public int CompletenessPercent { get; set; }
    public bool HasContactError { get; set; }
    public bool ChangeReported { get; set; }
    public bool IsDemo { get; set; }

    public Guid? CreatedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid? MergedIntoId { get; set; }

    public List<BusinessSource> Sources { get; set; } = [];
    public List<FieldProvenance> Provenances { get; set; } = [];
    public List<BusinessAssignment> Assignments { get; set; } = [];
}

/// <summary>Where a business (or part of its data) came from. Provenance is never discarded, even on merge.</summary>
public class BusinessSource : Entity
{
    public Guid BusinessId { get; set; }
    public Business? Business { get; set; }
    public SourceType SourceType { get; set; }
    /// <summary>Provider name, e.g. "google_places", "import:fichier.csv".</summary>
    public string Provider { get; set; } = string.Empty;
    /// <summary>Provider's own identifier (place_id, row key…): strongest duplicate signal.</summary>
    public string? ExternalId { get; set; }
    public string? Url { get; set; }
    public Guid? ImportBatchId { get; set; }
    public DateTime CollectedAt { get; set; }
}

/// <summary>Per-field origin: tells confirmed vs external vs manual vs estimated data apart.</summary>
public class FieldProvenance
{
    public Guid BusinessId { get; set; }
    public string Field { get; set; } = string.Empty;
    public FieldOrigin Origin { get; set; }
    public Guid? SourceId { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}

public class BusinessDataHistory : Entity
{
    public Guid BusinessId { get; set; }
    public string Field { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public FieldOrigin Origin { get; set; }
    public Guid? ChangedByUserId { get; set; }
    public string? Reason { get; set; }
}

public class BusinessAssignment : Entity
{
    public Guid BusinessId { get; set; }
    public Business? Business { get; set; }
    public Guid UserId { get; set; }
    public Guid? AssignedByUserId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? EndedAt { get; set; }
}

public class DuplicateCandidate : Entity
{
    /// <summary>Always stored with BusinessAId &lt; BusinessBId so a pair exists once.</summary>
    public Guid BusinessAId { get; set; }
    public Business? BusinessA { get; set; }
    public Guid BusinessBId { get; set; }
    public Business? BusinessB { get; set; }
    public double Score { get; set; }
    public string Reasons { get; set; } = string.Empty;
    public DuplicateStatus Status { get; set; } = DuplicateStatus.Pending;
    public Guid? ResolvedByUserId { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
