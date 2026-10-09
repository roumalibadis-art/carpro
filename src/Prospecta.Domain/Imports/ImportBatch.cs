using Prospecta.Domain.Common;

namespace Prospecta.Domain.Imports;

public class ImportBatch : Entity
{
    public string FileName { get; set; } = string.Empty;
    public string FileSha256 { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public ImportStatus Status { get; set; } = ImportStatus.Uploaded;
    /// <summary>JSON array of the file's header names (for the mapping UI).</summary>
    public string HeadersJson { get; set; } = "[]";
    /// <summary>JSON object: system field → file column.</summary>
    public string MappingJson { get; set; } = "{}";
    public int TotalRows { get; set; }
    public int ValidRows { get; set; }
    public int InvalidRows { get; set; }
    public int DuplicateRows { get; set; }
    public int ImportedRows { get; set; }
    public DateTime? CommittedAt { get; set; }
    public List<ImportRow> Rows { get; set; } = [];
}

public class ImportRow
{
    public long Id { get; set; }
    public Guid BatchId { get; set; }
    public int RowNumber { get; set; }
    public string RawJson { get; set; } = "{}";
    public ImportRowStatus Status { get; set; } = ImportRowStatus.Pending;
    public string? Errors { get; set; }
    public Guid? PotentialDuplicateOfId { get; set; }
    public Guid? ImportedBusinessId { get; set; }
}
