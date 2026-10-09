using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Prospecta.Domain.Auditing;
using Prospecta.Domain.Businesses;
using Prospecta.Domain.Geography;
using Prospecta.Domain.Imports;

namespace Prospecta.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<GeographicArea> GeographicAreas { get; }
    DbSet<BusinessCategory> BusinessCategories { get; }
    DbSet<StatusValue> StatusValues { get; }
    DbSet<Business> Businesses { get; }
    DbSet<BusinessSource> BusinessSources { get; }
    DbSet<FieldProvenance> FieldProvenances { get; }
    DbSet<BusinessDataHistory> BusinessHistory { get; }
    DbSet<BusinessAssignment> BusinessAssignments { get; }
    DbSet<DuplicateCandidate> DuplicateCandidates { get; }
    DbSet<ImportBatch> ImportBatches { get; }
    DbSet<ImportRow> ImportRows { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<SavedFilter> SavedFilters { get; }
    DbSet<Identity.ApplicationUser> AppUsers { get; }
    DatabaseFacade Database { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public interface ICurrentUser
{
    Guid? Id { get; }
    string? UserName { get; }
    string? IpAddress { get; }
    bool IsAuthenticated { get; }
    bool HasPermission(string permission);
}

public interface IAuditService
{
    /// <summary>Adds an audit row to the current unit of work (persisted by the caller's SaveChanges).</summary>
    void Record(string action, string entityType, object? entityId, string? details = null);
}

/// <summary>Hook for later phases (visits, follow-ups…) to move their data when two businesses are merged.</summary>
public interface IBusinessMergeParticipant
{
    Task MergeAsync(Guid survivorId, Guid mergedId, CancellationToken ct);
}

public interface IPermissionStore
{
    Task<IReadOnlySet<string>> GetForRolesAsync(IEnumerable<string> roles, CancellationToken ct = default);
    void Invalidate();
}
