using Prospecta.Domain.Common;

namespace Prospecta.Domain.Prospecting;

/// <summary>A saved report: its parameters and an immutable snapshot of the figures at generation time (never silently recomputed).</summary>
public class Report : Entity
{
    public ReportType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public Guid OwnerUserId { get; set; }
    public string ParametersJson { get; set; } = "{}";
    /// <summary>Private by default: only the owner reads it until they share it with their hierarchy.</summary>
    public bool Shared { get; set; }
    public Guid? SourceReportId { get; set; }
    public Guid? ValidatedByUserId { get; set; }
    public DateTime? ValidatedAt { get; set; }
    public string? ValidationNote { get; set; }
    public List<ReportSnapshot> Snapshots { get; set; } = [];
}

public class ReportSnapshot : Entity
{
    public Guid ReportId { get; set; }
    public Report? Report { get; set; }
    public string ContentJson { get; set; } = "{}";
}
