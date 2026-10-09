using Prospecta.Domain.Common;

namespace Prospecta.Domain.Geography;

/// <summary>Self-referencing hierarchy Wilaya → Daïra → Commune → Quartier (admin-managed, never hard-coded).</summary>
public class GeographicArea : Entity
{
    public GeoLevel Level { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public Guid? ParentId { get; set; }
    public GeographicArea? Parent { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Activity tree (two levels: main activity → sub-activity).</summary>
public class BusinessCategory : Entity
{
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public Guid? ParentId { get; set; }
    public BusinessCategory? Parent { get; set; }
    /// <summary>OpenStreetMap tag filters for free collection, e.g. "amenity=car_rental;shop=car_rental" (administrable).</summary>
    public string? OsmFilter { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Configurable status value for one of the three independent state dimensions.</summary>
public class StatusValue : Entity
{
    public StatusKind Kind { get; set; }
    /// <summary>Stable machine code; system codes are referenced by business rules.</summary>
    public string Code { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsSystem { get; set; }
}

public static class StatusCodes
{
    public const string Unassigned = "unassigned";
    public const string Assigned = "assigned";
    public const string ToVerify = "to_verify";
    public const string Verified = "verified";
    public const string Partial = "partial";
    public const string PotentialDuplicate = "potential_duplicate";
    public const string Closed = "closed";
    public const string Pending = "pending";
}
