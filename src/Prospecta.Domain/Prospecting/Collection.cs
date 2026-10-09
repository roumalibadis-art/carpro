using Prospecta.Domain.Common;

namespace Prospecta.Domain.Prospecting;

/// <summary>One run of a collection connector (search by zone and activity). Logged whatever the outcome.</summary>
public class DataCollectionJob : Entity
{
    public string ConnectorKey { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string ParametersJson { get; set; } = "{}";
    public CollectionJobStatus Status { get; set; } = CollectionJobStatus.Running;
    public DateTime? FinishedAt { get; set; }
    public int Found { get; set; }
    public int NewCount { get; set; }
    public int DuplicateCount { get; set; }
    public int ImportedCount { get; set; }
    public int SkippedCount { get; set; }
    public string? Message { get; set; }
    public Guid? RetryOfJobId { get; set; }
}

public class DataCollectionJobResult : Entity
{
    public Guid JobId { get; set; }
    public DataCollectionJob? Job { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public CollectionResultStatus Status { get; set; } = CollectionResultStatus.New;
    /// <summary>Normalized candidate (JSON) shown in the review screen and used to create the business.</summary>
    public string PayloadJson { get; set; } = "{}";
    public Guid? MatchBusinessId { get; set; }
    public string? MatchReason { get; set; }
    public Guid? BusinessId { get; set; }
}

/// <summary>Per-connector, per-day call counter: quota and rate-limit bookkeeping.</summary>
public class ConnectorUsage
{
    public string ConnectorKey { get; set; } = string.Empty;
    public DateOnly Day { get; set; }
    public int Calls { get; set; }
    public DateTime? LastCallAt { get; set; }
}
