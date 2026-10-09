namespace Prospecta.Domain.Common;

public enum GeoLevel { Wilaya = 1, Daira = 2, Commune = 3, Quartier = 4 }

/// <summary>The three independent state dimensions of a business (never mixed up).</summary>
public enum StatusKind { Census = 1, Processing = 2, Outcome = 3 }

/// <summary>Provenance of a single field value: what kind of statement it is.</summary>
public enum FieldOrigin { Confirmed = 1, External = 2, Manual = 3, Estimated = 4 }

public enum SourceType
{
    GooglePlaces = 1,
    PublicWebsite = 2,
    OfficialApi = 3,
    MetaPages = 4,
    OtherPublic = 5,
    FileImport = 6,
    Manual = 7,
    PublicUrl = 8,
    Demo = 9,
}

public enum Priority { Low = 1, Normal = 2, High = 3 }

public enum ConfidenceLevel { Low = 1, Medium = 2, High = 3 }

public enum DuplicateStatus { Pending = 1, NotDuplicate = 2, Merged = 3 }

public enum ImportStatus { Uploaded = 1, Mapped = 2, Committed = 3, Cancelled = 4 }

public enum ImportRowStatus { Pending = 1, Valid = 2, Invalid = 3, PotentialDuplicate = 4, Imported = 5, Skipped = 6 }

public enum SavedFilterScope { Personal = 1, Shared = 2 }
